using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.Builtin_ComfyUIBackend;
using Newtonsoft.Json.Linq;

namespace H3FaceRefine;

public class H3FaceRefineExtension : Extension
{
    public static T2IParamGroup H3FaceRefineGroup;

    // Execution parameters
    public static T2IRegisteredParam<string> H3FaceRefineTarget;
    public static T2IRegisteredParam<double> H3FaceRefineDenoise;
    public static T2IRegisteredParam<double> H3FaceRefineMultSmall;
    public static T2IRegisteredParam<double> H3FaceRefineMultLarge;
    public static T2IRegisteredParam<int> H3FaceRefineSteps;
    public static T2IRegisteredParam<string> H3FaceRefineCanvasSize;
    public static T2IRegisteredParam<int> H3FaceRefineFeather;
    public static T2IRegisteredParam<double> H3FaceRefineConfidence;
    public static T2IRegisteredParam<string> H3FaceRefinePrompt;
    public static T2IRegisteredParam<Image> H3FaceRefineReferenceImage;
    public static T2IRegisteredParam<Image> H3FaceRefineReferenceImage2;
    public static T2IRegisteredParam<string> H3FaceRefineRefSize;
    public static T2IRegisteredParam<bool> H3FaceRealignPaste;
    public static T2IRegisteredParam<double> H3FaceAlignStrength;
    public static T2IRegisteredParam<int> H3FaceAlignSmooth;
    public static T2IRegisteredParam<double> H3FaceAlignMaxShift;

    // Internal history video parameter
    public static T2IRegisteredParam<string> H3FaceRefineVideoFile;

    public static readonly string[] TargetOptions =
    [
        "Both Faces (Chained)",
        "Primary Face (Largest)",
        "Secondary Face",
        "Left Person",
        "Right Person",
        "Center Person",
        "Identity Match (Reference Image)"
    ];

    /// <summary>Default target mode, matching the registration default.</summary>
    public const string DefaultTarget = "Primary Face (Largest)";

    /// <summary>Defaults shared by the parameter registration, the generation code, and the button JS defaults (assets/h3_facerefine.js - keep in sync there, JS cannot read C# constants).</summary>
    public static class H3FaceRefineDefaults
    {
        /// <summary>Default face-regeneration denoise.</summary>
        public const double Denoise = 0.27;

        /// <summary>Default small-face denoise multiplier.</summary>
        public const double MultSmall = 1.0;

        /// <summary>Default large-face denoise multiplier.</summary>
        public const double MultLarge = 0.65;

        /// <summary>Default sampling steps.</summary>
        public const int Steps = 6;

        /// <summary>Default composite feather radius, in source pixels.</summary>
        public const int Feather = 22;

        /// <summary>Default face-detection confidence.</summary>
        public const double Confidence = 0.35;

        /// <summary>Default auto-align correction strength.</summary>
        public const double AlignStrength = 1.0;

        /// <summary>Default auto-align temporal smoothing window (frames, odd).</summary>
        public const int AlignSmooth = 13;

        /// <summary>Default auto-align max shift, in crop pixels.</summary>
        public const double AlignMaxShift = 40.0;
    }

    /// <summary>Formats a numeric default as its invariant-culture string for parameter registration.</summary>
    private static string Num(double value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a numeric default as its invariant-culture string for parameter registration.</summary>
    private static string Num(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Compiled once and reused: the &lt;Picture i&gt;/&lt;Video k&gt;/&lt;Audio j&gt; reference tags of the
    /// original generation that must be stripped from refine prompts. Static readonly so the regex
    /// is not re-parsed on every prompt sanitize (and never inside a per-frame loop).
    /// </summary>
    private static readonly Regex MediaReferenceTagRegex = new(@"<\s*[Pp]icture\s+\d+\s*>|<\s*[Vv]ideo\s+\d+\s*>|<\s*[Aa]udio\s+\d+\s*>", RegexOptions.Compiled);

    /// <summary>Compiled once and reused: collapses runs of spaces/tabs left behind by stripped tags.</summary>
    private static readonly Regex WhitespaceCollapseRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Finds an installed MiniMax H3 model by scanning every registered model set.
    /// NOTE: this PR's proposed caching (invalidate via T2IModelSet change events) was DROPPED —
    /// T2IModelSet/LocalPath/Added do not exist in this SwarmUI, and the linear scan costs
    /// milliseconds next to a multi-minute video job.
    /// </summary>
    private static T2IModel FindCachedH3Model()
    {
        return Program.T2IModelSets.Values
            .SelectMany(set => set.Models?.Values ?? Enumerable.Empty<T2IModel>())
            .FirstOrDefault(m => m.ModelClass?.ID == T2IModelClassSorter.CompatMiniMaxH3.ID);
    }

    public override void OnInit()
    {
        Logs.Init("MiniMax H3 Face Refine Extension initializing...");

        // Map ComfyUI node to feature set
        ComfyUIBackendExtension.NodeToFeatureMap["H3FaceTrackCrop"] = "h3_face_refine";
        ComfyUIBackendExtension.NodeToFeatureMap["SwarmH3FaceRealign"] = "h3_face_refine";
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3NativeAudioLock"] = "h3_audio_lock";

        // Register installable features if missing
        InstallableFeatures.RegisterInstallableFeature(new(
            "MiniMax H3 Face Refine Nodes",
            "h3_face_refine",
            "https://github.com/Carasibana/ComfyUI-H3-FaceRefine",
            "comfyui",
            "This will install the ComfyUI-H3-FaceRefine node pack for tracking and restoring faces in MiniMax H3 videos.\nDo you wish to install?"
        ));
        InstallableFeatures.RegisterInstallableFeature(new(
            "MiniMax H3 Native Audio Lock",
            "h3_audio_lock",
            "https://github.com/Shrek3OnVH5/MiniMax-H3-NativeAudio-MusicVideo-Workflow",
            "comfyui",
            "This will install the node pack that provides MiniMaxH3NativeAudioLock, which locks the source audio into the joint AV latent during face refinement so the model does not invent its own.\nThe node pack lives under custom_nodes/ComfyUI-H3-NativeAudioLock inside that repository.\nDo you wish to install?"
        ));

        // Add history / media action JS
        ScriptFiles.Add("assets/h3_facerefine.js");

        RegisterParameters();

        // Register workflow step at priority -5.9 (intercept standalone video file requests before sampler/refiner)
        WorkflowGenerator.AddStep(GenerateH3FaceRefineWorkflow, -5.9);

        // Register workflow step at priority 45 (live video generation face refinement)
        WorkflowGenerator.AddStep(GenerateH3FaceRefineLiveWorkflow, 45);

        Logs.Init("MiniMax H3 Face Refine Extension loaded successfully.");
    }

    public override void PopulateMetadata()
    {
        Version = "1.0.0";
        ExtensionAuthor = "Tonetxo";
        Description = "MiniMax H3 Face Refiner: tracks faces in videos and restores facial identities.";
    }

    private static void RegisterParameters()
    {
        H3FaceRefineGroup = new(
            Name: "H3 Face Refine",
            Description: "Refine and restore small or distant faces in MiniMax H3 videos.\n" +
                         "Tracks faces across frames, regenerates them with MiniMax H3 DiT at low denoise, and seamlessly composites them back with feathered borders.\n" +
                         "Supports single and two-person shots (chained sequential refinement).",
            Toggles: true,
            Open: false,
            OrderPriority: 8.5,
            IsAdvanced: false
        );

        double priority = 0;

        H3FaceRefineTarget = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "H3 Face Target",
            Description: "Select which face(s) to refine:\n" +
                         "- 'Both Faces (Chained)': Refines the left and right subjects sequentially in one pass.\n" +
                         "- 'Primary Face (Largest)': Focuses on the most prominent face.\n" +
                         "- 'Secondary Face': Refines the second largest face.\n" +
                         "- 'Left Person' / 'Right Person': For dialog or 2-shot scenes.\n" +
                         "- 'Center Person': Focuses on the central subject.\n" +
                         "- 'Identity Match (Reference Image)': Locks onto the subject matching Reference Image 1 via InsightFace embeddings.",
            Default: DefaultTarget,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            GetValues: _ => TargetOptions.ToList()
        ));

        H3FaceRefineDenoise = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Denoise",
            Description: "Denoising strength for the face region on H3's flow-matching schedule. " +
                         "Unlike SDXL, the value maps to a noise fraction of the sigma schedule: 0.30 repaints about 30% of the face content per frame. " +
                         "For RESTORING a face keep the Large-Face Multiplier low (default): big faces are protected. " +
                         "For SUBSTITUTING a face with a reference portrait of ANOTHER person, set 'Denoise Large Face' to 1.0 and raise this to 0.5-0.65 - measured: 0.5 already transfers distinguishing features (eg facial hair), 0.65 transfers a fully different identity (full beard) while keeping the head motion.",
            Default: Num(H3FaceRefineDefaults.Denoise),
            Min: 0.05,
            Max: 0.80,
            Step: 0.01,
            ViewType: ParamViewType.SLIDER,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true
        ));

        H3FaceRefineMultSmall = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Denoise Small Face",
            Description: "MULTIPLIER on the denoise for the SMALLEST faces (they have no real detail to preserve and benefit most from the pass). Keep at 1.0.",
            Default: Num(H3FaceRefineDefaults.MultSmall),
            Min: 0.0,
            Max: 1.0,
            Step: 0.05,
            ViewType: ParamViewType.SLIDER,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceRefineMultLarge = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Denoise Large Face",
            Description: "MULTIPLIER on the denoise for the LARGEST faces (>= ~120px source height). Low values protect the real detail of a close-up face - the restore default. For face SUBSTITUTION with a reference of a different person, set 1.0 with denoise 0.5-0.65, or the repaint is too gentle to transfer the new identity.",
            Default: Num(H3FaceRefineDefaults.MultLarge),
            Min: 0.0,
            Max: 1.0,
            Step: 0.05,
            ViewType: ParamViewType.SLIDER,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceRefineSteps = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "H3 Face Steps",
            Description: "Sampling steps for the MiniMax H3 DiT refiner. Typically 4 to 8 steps (6 recommended).",
            Default: Num(H3FaceRefineDefaults.Steps),
            Min: 1,
            Max: 20,
            Step: 1,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true
        ));

        H3FaceRefineCanvasSize = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "H3 Face Canvas Size",
            Description: "Canvas resolution the face is normalized to during generation.\n" +
                         "'auto_capped_768' (recommended) scales up to 768px for maximum detail while keeping VRAM low.\n" +
                         "'512' is faster and lighter.",
            Default: "auto_capped_768",
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true,
            GetValues: _ => ["auto_capped_768", "768", "512"]
        ));

        H3FaceRefineFeather = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "H3 Face Feather",
            Description: "Gaussian blur feather radius in source pixels applied to the composite paste mask. 18-24 provides soft, invisible edge blending.",
            Default: Num(H3FaceRefineDefaults.Feather),
            Min: 2,
            Max: 64,
            Step: 2,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceRefineConfidence = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Detection Confidence",
            Description: "YOLO detection confidence threshold (0.35 default). Lower detects partially occluded or distant faces.",
            Default: Num(H3FaceRefineDefaults.Confidence),
            Min: 0.10,
            Max: 0.90,
            Step: 0.05,
            ViewType: ParamViewType.SLIDER,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceRefinePrompt = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "H3 Face Prompt",
            Description: "Optional facial prompt override (e.g. 'high quality detailed face, realistic eyes, natural skin texture'). If empty, uses the video's original prompt.",
            Default: "",
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true
        ));

        H3FaceRefineReferenceImage = T2IParamTypes.Register<Image>(new T2IParamType(
            Name: "H3 Face Reference Image",
            Description: "[Optional] Reference portrait/photo for Person 1 (or Left Person in dual-face workflows).\n" +
                         "- In H3FaceTrackCrop: Uses InsightFace to lock onto this person's identity across the entire video.\n" +
                         "- In MiniMax H3: Conditions the DiT on this reference portrait (<Picture 1>) to transfer exact facial features, likeness, and high-frequency details.",
            Default: null,
            Toggleable: true,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine"
        ));

        H3FaceRefineReferenceImage2 = T2IParamTypes.Register<Image>(new T2IParamType(
            Name: "H3 Face Reference Image Two",
            Description: "[Optional] Reference portrait/photo for Person 2 (or Right Person in dual-face workflows).\n" +
                         "- In H3FaceTrackCrop: Uses InsightFace to lock onto Person 2's identity across the entire video.\n" +
                         "- In MiniMax H3: Conditions the DiT on this reference portrait (<Picture 1>) during stage 2.",
            Default: null,
            Toggleable: true,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine"
        ));

        H3FaceRefineRefSize = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "H3 Face Ref Image Size",
            Description: "How the reference portrait is conditioned for identity transfer.\n" +
                         "'match' scales the reference down to the generation's canvas area (faster, default).\n" +
                         "'max' conditions at the reference pipeline's full 2048px short edge for best identity fidelity - use it when replacing a face with a different person. Note that reference tokens ride through every sampling step, so 'max' can be several times slower.",
            Default: "match",
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true,
            GetValues: _ => ["match", "max"]
        ));

        H3FaceRealignPaste = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "H3 Face Auto-Align Paste",
            Description: "Detects the face in each regenerated crop and in the matching source crop, then shifts the regenerated one so the two faces coincide before stitching - cancelling the few-pixel content offset H3 leaves inside the paste (the visible 'sliding face'). Translation only, so a substituted identity is not distorted. Leave on.",
            Default: "true",
            Toggleable: true,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true
        ));

        H3FaceAlignStrength = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Align Strength",
            Description: "How much of the auto-align correction to apply (1.0 = full). Lower it if the refined face still reads as wobbling inside the paste at high denoise.",
            Default: Num(H3FaceRefineDefaults.AlignStrength),
            Min: 0.0,
            Max: 1.0,
            Step: 0.05,
            ViewType: ParamViewType.SLIDER,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceAlignSmooth = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "H3 Face Align Smooth Frames",
            Description: "Gaussian window smoothing the align correction over time (odd). Higher = calmer motion, but a lagging correction if the face genuinely moves fast.",
            Default: Num(H3FaceRefineDefaults.AlignSmooth),
            Min: 1,
            Max: 51,
            Step: 2,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceAlignMaxShift = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Align Max Shift",
            Description: "Skip the align on frames whose two face centres are farther apart than this (pixels, crop space) - bigger gaps are detected as mismatch detection or tracking breakage, not an offset.",
            Default: Num(H3FaceRefineDefaults.AlignMaxShift),
            Min: 10.0,
            Max: 200.0,
            Step: 5.0,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            IsAdvanced: true
        ));

        H3FaceRefineVideoFile = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "H3 Face Refine Video File",
            Description: "Internal parameter used by the 'H3 Face Refine' button on completed videos.",
            Default: "",
            VisibleNormally: false,
            DoNotSave: true,
            Nonreusable: true,
            HideFromMetadata: true
        ));
    }

    /// <summary>
    /// Normalizes the 'H3 Face Ref Image Size' value to the node's accepted set ('match'/'max').
    /// Unknown or empty values fall back to the 'match' default (fastest, lowest VRAM).
    /// </summary>
    private static string NormalizeRefSize(string raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "max" => "max",
            _ => "match"
        };
    }

    /// <summary>Returns the path unchanged if it stays inside 'root', otherwise a safe fallback path under root.</summary>
    private static string SanitizeRelativeWithinRoot(string path, string root, string fallbackName)
    {
        string full = Path.GetFullPath(Utilities.CombinePathWithAbsolute(root, path));
        string rootFull = Path.GetFullPath(root);
        if (full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar))
        {
            return full;
        }
        Logs.Warning($"H3 Face Refine: rejected path traversal attempt '{path}' outside of user output root.");
        return Path.Combine(rootFull, fallbackName);
    }

    private static string ResolveVideoPath(User user, string videoFile)
    {
        // Normalize separators so the prefix checks below also work on Windows paths.
        videoFile = videoFile.Replace('\\', '/');

        if (videoFile.StartsWith("~/"))
        {
            videoFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + videoFile[1..];
        }

        if (user is not null)
        {
            string relativePath = null;
            if (videoFile.StartsWith("Output/"))
            {
                relativePath = videoFile["Output/".Length..];
            }
            else if (videoFile.StartsWith("View/"))
            {
                string afterView = videoFile["View/".Length..];
                int slashIndex = afterView.IndexOf('/');
                if (slashIndex > 0)
                {
                    relativePath = afterView[(slashIndex + 1)..];
                }
            }

            if (relativePath is not null)
            {
                string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
                // Defense-in-depth: never let a client-supplied relative path escape the user's own output root.
                relativePath = SanitizeRelativeWithinRoot(relativePath, root, "h3_facerefine_rejected.mp4");
                return UserImageHistoryHelper.GetRealPathFor(user, relativePath, root: root);
            }
        }

        return videoFile;
    }

    private static (string container, string codec) GetVideoFormat(WorkflowGenerator g)
    {
        string swarmVideoFormat = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4");
        // Normalize user-supplied format so weird casing/spacing cannot smuggle unexpected node values.
        swarmVideoFormat = swarmVideoFormat.ToLowerInvariant().Trim();
        string container = "mp4";
        string codec = "auto";
        if (swarmVideoFormat.Contains("-"))
        {
            string[] parts = swarmVideoFormat.Split('-');
            codec = parts[0];
            container = parts.Length > 1 ? parts[1] : "mp4";
        }
        else
        {
            container = swarmVideoFormat;
        }

        if (container != "mp4" && container != "auto")
        {
            container = "mp4";
        }
        if (codec != "h264" && codec != "auto")
        {
            codec = "auto";
        }
        return (container, codec);
    }

    public static void GenerateH3FaceRefineWorkflow(WorkflowGenerator g)
    {
        bool hasVideoFile = g.UserInput.TryGet(H3FaceRefineVideoFile, out string videoFile) && !string.IsNullOrEmpty(videoFile);
        if (!hasVideoFile)
        {
            return;
        }

        g.UserInput.Remove(H3FaceRefineVideoFile);

        // Disarm any active refiner or init image so on-demand video history processing is 100% isolated
        g.UserInput.Remove(T2IParamTypes.RefinerMethod);
        g.UserInput.Remove(T2IParamTypes.RefinerModel);
        g.UserInput.Remove(T2IParamTypes.InitImage);

        if (!g.Features.Contains("h3_face_refine"))
        {
            throw new SwarmUserErrorException("H3 Face Refine requires the ComfyUI-H3-FaceRefine node pack. Please ensure it is installed and enabled.");
        }

        User user = g.UserInput.SourceSession?.User;
        string resolvedPath = ResolveVideoPath(user, videoFile);
        if (!File.Exists(resolvedPath))
        {
            throw new SwarmUserErrorException($"H3 Face Refine: Video file not found: {resolvedPath}");
        }

        if (!g.IsMiniMaxH3())
        {
            // Search every model set - avoids hardcoding set names ("Diffusion Models", etc.) that may change upstream.
            // Scans every model set (no hardcoded set names that may change upstream).
            T2IModel h3Model = FindCachedH3Model();

            if (h3Model is not null)
            {
                Logs.Info($"H3 Face Refine: Switching model to '{h3Model.Name}' for face refinement pass.");
                g.UserInput.Set(T2IParamTypes.Model, h3Model);
            }
            else
            {
                throw new SwarmUserErrorException("H3 Face Refine requires a MiniMax H3 model. Please select a MiniMax H3 model in the Models dropdown.");
            }
        }

        Logs.Info($"H3 Face Refine: Processing video '{resolvedPath}'");

        // Read through a using-scoped stream so the file handle is always released, even on failure.
        // FileStream with a large buffer + File.ReadExactly avoids per-chunk loop overhead and
        // intermediate array copies that File.ReadAllBytes/BufferedStream paths incur for big videos.
        byte[] rawVideoData;
        try
        {
            using FileStream stream = new(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan);
            rawVideoData = new byte[stream.Length];
            stream.ReadExactly(rawVideoData);
        }
        catch (Exception ex)
        {
            throw new SwarmUserErrorException($"H3 Face Refine: failed to read video file '{resolvedPath}': {ex.Message}");
        }
        VideoFile sourceVideo = new(rawVideoData, MediaType.GetByExtension(resolvedPath.AfterLast('.').ToLowerFast()));

        WGNodeData videoData = g.LoadVideo(sourceVideo, "${h3_face_refine_source_video}", false);
        JArray currentFrames = videoData.Path;
        JArray videoAudio = videoData.AttachedAudio?.Path;
        JToken videoFps = videoData.FPS;

        JArray finalFrames = ExecuteFaceRefinementPipeline(g, currentFrames, videoAudio);

        string createVideoNode = g.CreateNode("CreateVideo", new JObject()
        {
            ["images"] = finalFrames,
            ["audio"] = videoAudio,
            ["fps"] = videoFps
        });

        var (container, codec) = GetVideoFormat(g);
        g.CreateNode("SaveVideo", new JObject()
        {
            ["video"] = new JArray() { createVideoNode, 0 },
            ["filename_prefix"] = "video/H3_FaceRefined",
            ["format"] = container,
            ["codec"] = codec
        });

        g.CurrentMedia = new WGNodeData(finalFrames, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        g.SkipFurtherSteps = true;
    }

    /// <summary>Workflow step for live in-pipeline face refinement during video generation (Priority 45).</summary>
    public static void GenerateH3FaceRefineLiveWorkflow(WorkflowGenerator g)
    {
        if (g.SkipFurtherSteps)
        {
            return;
        }

        // Do not run if standalone history video processing was requested
        if (g.UserInput.TryGet(H3FaceRefineVideoFile, out string vf) && !string.IsNullOrEmpty(vf))
        {
            return;
        }

        // SwarmUI groups with Toggles: true only set their parameters in g.UserInput when enabled
        if (!g.UserInput.TryGet(H3FaceRefineTarget, out string targetMode) &&
            !g.UserInput.TryGet(H3FaceRefineDenoise, out _) &&
            !g.UserInput.TryGet(H3FaceRefineSteps, out _))
        {
            return;
        }

        if (!g.IsMiniMaxH3())
        {
            return;
        }

        if (g.CurrentMedia is null)
        {
            return;
        }

        if (!g.Features.Contains("h3_face_refine"))
        {
            throw new SwarmUserErrorException("H3 Face Refine is enabled, but the ComfyUI-H3-FaceRefine node pack is not installed or available on this ComfyUI backend.");
        }

        Logs.Info($"H3 Face Refine: Applying live in-pipeline face refinement (Target: {targetMode ?? "Both Faces (Chained)"})");

        // Ensure decoded frames are available as raw image batch
        g.CurrentMedia = g.CurrentMedia.AsRawImage(g.CurrentVae);
        JArray refinedFrames = ExecuteFaceRefinementPipeline(g, g.CurrentMedia.Path, g.CurrentMedia.AttachedAudio?.Path);

        // Update CurrentMedia preserving attached audio, FPS, etc.
        g.CurrentMedia = g.CurrentMedia.WithPath(refinedFrames);
        Logs.Info("H3 Face Refine: Live face refinement pass successfully connected into generation pipeline.");
    }

    /// <summary>
    /// Prepares the facial prompt for the refine pass. The original video prompt's
    /// &lt;Picture i&gt;/&lt;Video k&gt;/&lt;Audio j&gt; tags point at reference inputs of the ORIGINAL
    /// generation, which this pass never sees - left in place they dangle and the DiT repaints the
    /// face toward a generic prior (lost expression, mask-like result). All such tags are stripped;
    /// when a reference portrait is wired it is re-anchored as the pass's own &lt;Picture 1&gt;.
    /// </summary>
    public static string SanitizeFacialPrompt(string rawPrompt, bool refTag)
    {
        string cleaned = rawPrompt ?? "";
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = MediaReferenceTagRegex.Replace(cleaned, " ");
            cleaned = WhitespaceCollapseRegex.Replace(cleaned, " ").Trim();
        }
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = refTag
                ? "photorealistic face of this person, realistic eyes, detailed facial features, natural skin texture"
                : "high quality detailed face, expressive eyes, realistic mouth, natural skin texture";
        }
        return refTag ? $"<Picture 1> {cleaned}" : cleaned;
    }

    /// <summary>Modular pipeline for tracking, cropping, DiT refining, and seamless face stitching.</summary>
    public static JArray ExecuteFaceRefinementPipeline(WorkflowGenerator g, JArray inputFrames, JArray audioNode = null)
    {
        string targetModeRaw = g.UserInput.Get(H3FaceRefineTarget, DefaultTarget);
        // Validate against the known option set; unknown values would otherwise silently fall
        // through to the Primary-Face branch below.
        if (!TargetOptions.Contains(targetModeRaw))
        {
            Logs.Warning($"H3 Face Refine: unknown H3 Face Target '{targetModeRaw}', falling back to '{DefaultTarget}'.");
            targetModeRaw = DefaultTarget;
        }
        double denoise = g.UserInput.Get(H3FaceRefineDenoise, H3FaceRefineDefaults.Denoise);
        int steps = g.UserInput.Get(H3FaceRefineSteps, H3FaceRefineDefaults.Steps);
        string canvasMode = g.UserInput.Get(H3FaceRefineCanvasSize, "auto_capped_768");
        int feather = g.UserInput.Get(H3FaceRefineFeather, H3FaceRefineDefaults.Feather);
        double confidence = g.UserInput.Get(H3FaceRefineConfidence, H3FaceRefineDefaults.Confidence);
        // Read the per-request settings that ProcessFaceStage needs exactly once, up front.
        // (These were previously re-read from UserInput - including a culture-sensitive string
        // parse on every call - inside each stage; hoisting them keeps stage building allocation-free.)
        string refImageSize = NormalizeRefSize(g.UserInput.Get(H3FaceRefineRefSize, "match"));
        double multSmall = g.UserInput.Get(H3FaceRefineMultSmall, H3FaceRefineDefaults.MultSmall);
        double multLarge = g.UserInput.Get(H3FaceRefineMultLarge, H3FaceRefineDefaults.MultLarge);
        bool autoAlignPaste = g.UserInput.Get(H3FaceRealignPaste, true);
        bool hasAudioLock = g.Features.Contains("h3_audio_lock") && g.CurrentAudioVae is not null;
        string targetMode = targetModeRaw; // validated above against TargetOptions
        bool hasRefImage1 = g.UserInput.TryGet(H3FaceRefineReferenceImage, out Image refImg1) && refImg1 is not null;
        bool hasRefImage2 = g.UserInput.TryGet(H3FaceRefineReferenceImage2, out Image refImg2) && refImg2 is not null;
        string userPromptSource = g.UserInput.Get(H3FaceRefinePrompt, "");
        if (string.IsNullOrWhiteSpace(userPromptSource))
        {
            userPromptSource = g.UserInput.Get(T2IParamTypes.Prompt, "")?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(userPromptSource))
            {
                userPromptSource = $"{userPromptSource}, highly detailed face, expressive eyes, realistic mouth, natural skin texture";
            }
        }
        // Strips dangled <Picture i>/<Video k>/<Audio j> references of the original generation;
        // per-stage the reference portrait is re-anchored as this pass's own <Picture 1>.
        string userPromptBase = SanitizeFacialPrompt(userPromptSource, refTag: false);

        if (inputFrames.Count > 362)
        {
            Logs.Warning($"H3 Face Refine: {inputFrames.Count} frames exceed MiniMax H3's trained range (~124-362). " +
                         "Quality beyond that range is untested (the model was trained on ~5-15s at 24fps) and may degrade or drift; " +
                         "consider splitting long videos before refining.");
        }

        long baseSeed = g.UserInput.Get(T2IParamTypes.Seed, 0);
        JArray refImageNode1 = null;
        if (hasRefImage1)
        {
            refImageNode1 = g.LoadImage(refImg1, "${h3_face_refine_reference_image}", false).Path;
            Logs.Info("H3 Face Refine: Reference portrait 1 loaded for Person 1.");
        }
        JArray refImageNode2 = null;
        if (hasRefImage2)
        {
            refImageNode2 = g.LoadImage(refImg2, "${h3_face_refine_reference_image_2}", false).Path;
            Logs.Info("H3 Face Refine: Reference portrait 2 loaded for Person 2.");
        }

        // Helper to process one face stage
        JArray ProcessFaceStage(JArray frames, string selectMode, int selectIndex, string stageLabel, long seedOffset, JArray stageRefImageNode, bool useIdentityTracking = false)
        {
            bool hasStageRef = stageRefImageNode is not null;
            bool trackByIdentity = useIdentityTracking && hasStageRef;
            Logs.Info($"H3 Face Refine: Adding stage '{stageLabel}' (select={selectMode}, index={selectIndex}, denoise={denoise}, steps={steps}, hasRef={hasStageRef}, trackByIdentity={trackByIdentity})");

            JObject trackCropInputs = new()
            {
                ["images"] = frames,
                ["detector"] = "bbox/face_yolov8m.pt",
                ["confidence"] = confidence,
                ["crop_factor"] = 3.0,
                ["canvas_width"] = 768,
                ["canvas_height"] = 768,
                ["canvas_mode"] = canvasMode,
                ["smooth_window"] = 21,
                ["size_smooth_window"] = 51,
                ["smooth_method"] = "gaussian",
                ["size_mode"] = "per_frame",
                ["identity_track"] = trackByIdentity,
                ["identity_threshold"] = 0.28,
                ["select"] = selectMode,
                ["fallback_detector"] = "segm/person_yolov8m-seg.pt",
                ["fallback_head_frac"] = 0.5,
                ["select_index"] = selectIndex,
                ["identity_model"] = "insightface",
                ["cut_detection"] = "none"
            };
            if (trackByIdentity)
            {
                trackCropInputs["identity_reference"] = stageRefImageNode;
            }
            string trackCropNode = g.CreateNode("H3FaceTrackCrop", trackCropInputs);
            JArray crops = [trackCropNode, 0];
            JArray transform = [trackCropNode, 1];
            JArray canvasW = [trackCropNode, 4];
            JArray canvasH = [trackCropNode, 5];
            JArray frameCount = [trackCropNode, 6];

            string stagePrompt = hasStageRef ? $"<Picture 1> {userPromptBase}" : userPromptBase;

            // The reference portrait is fed to MiniMax H3 RAW - do not re-normalize it through
            // H3FaceTrackCrop and do not anchor the first frame as a keyframe. Both were tried
            // (to stop content drift and hallucinations) and both were measured to DESTROY
            // identity transfer: re-cropping the ref shrinks its face into the video crop's
            // composition, and a real first-frame keyframe pins the original person's face,
            // dragging the temporal attention back to the source identity. Runs without either
            // transfer the reference's features (eg facial hair) at denoise 0.5+; runs with
            // either one reproduce the source face exactly. The old "cat on the bust"
            // hallucination was fixed by the prompt sanitize + denoise recipe, not by these.
            JObject ref2vidInputs = new()
            {
                ["clip"] = g.CurrentTextEnc.Path,
                ["prompt"] = stagePrompt,
                ["width"] = canvasW,
                ["height"] = canvasH,
                ["length"] = frameCount,
                ["ref_image_size"] = refImageSize,
                ["vae"] = g.CurrentVae.Path
            };
            if (hasStageRef)
            {
                ref2vidInputs["ref_images.ref_image_0"] = stageRefImageNode;
            }
            if (g.CurrentAudioVae is not null)
            {
                ref2vidInputs["audio_vae"] = g.CurrentAudioVae.Path;
            }
            string ref2vidNode = g.CreateNode("MiniMaxH3ReferenceToVideo", ref2vidInputs);
            JArray positiveCond = [ref2vidNode, 0];
            JArray emptyLatent = [ref2vidNode, 1];

            JObject injectInputs = new()
            {
                ["av_latent"] = emptyLatent,
                ["images"] = crops,
                ["vae"] = g.CurrentVae.Path
            };
            string injectNode = g.CreateNode("H3InjectVideoLatent", injectInputs);
            JArray injectedLatent = [injectNode, 0];

            // Lock the source audio into the joint AV latent so the model denoises video only
            // while attending to the real soundtrack. Without it the audio stream is empty and
            // H3 generates its own audio, whose cross-attention desynchronizes the regenerated
            // face's mouth from the original performance.
            JArray samplerModel = g.CurrentModel.Path;
            JArray samplerLatent = injectedLatent;
            if (audioNode is not null && hasAudioLock)
            {
                string lockNode = g.CreateNode("MiniMaxH3NativeAudioLock", new JObject()
                {
                    ["model"] = g.CurrentModel.Path,
                    ["av_latent"] = injectedLatent,
                    ["audio_vae"] = g.CurrentAudioVae.Path,
                    ["audio"] = audioNode
                });
                samplerModel = [lockNode, 0];
                samplerLatent = [lockNode, 1];
                Logs.Info("H3 Face Refine: MiniMaxH3NativeAudioLock wired (source audio locked, video-only denoise).");
            }

            JObject perFrameInputs = new()
            {
                ["model"] = samplerModel,
                ["av_latent"] = samplerLatent,
                ["transform"] = transform,
                ["denoise_multiplier_small_face"] = multSmall,
                ["denoise_multiplier_large_face"] = multLarge,
                ["scale_mode"] = "absolute_px",
                ["face_px_small"] = 30.0,
                ["face_px_large"] = 120.0,
                ["gamma"] = 1.0,
                ["smooth_frames"] = 9
            };
            string perFrameNode = g.CreateNode("H3PerFrameDenoise", perFrameInputs);
            JArray denoisedLatent = [perFrameNode, 0];
            JArray patchedModel = [perFrameNode, 2];

            string schedulerNode = g.CreateNode("BasicScheduler", new JObject()
            {
                ["model"] = patchedModel,
                ["scheduler"] = "simple",
                ["steps"] = steps,
                ["denoise"] = denoise
            });
            JArray sigmas = [schedulerNode, 0];

            string guiderNode = g.CreateNode("BasicGuider", new JObject()
            {
                ["model"] = patchedModel,
                ["conditioning"] = positiveCond
            });
            JArray guider = [guiderNode, 0];

            string samplerSelectNode = g.CreateNode("KSamplerSelect", new JObject()
            {
                ["sampler_name"] = "euler"
            });
            JArray sampler = [samplerSelectNode, 0];

            string noiseNode = g.CreateNode("RandomNoise", new JObject()
            {
                ["noise_seed"] = baseSeed + seedOffset
            });
            JArray noise = [noiseNode, 0];

            string advancedSamplerNode = g.CreateNode("SamplerCustomAdvanced", new JObject()
            {
                ["noise"] = noise,
                ["guider"] = guider,
                ["sampler"] = sampler,
                ["sigmas"] = sigmas,
                ["latent_image"] = denoisedLatent
            });
            JArray refinedLatent = [advancedSamplerNode, 0];

            string decodeNode = g.CreateNode("VAEDecode", new JObject()
            {
                ["samples"] = refinedLatent,
                ["vae"] = g.CurrentVae.Path
            });
            JArray refinedCrops = [decodeNode, 0];

            // H3's repaint re-frames the face content inside the crop by a few pixels while the
            // paste position itself is exact - which reads as the refined face sliding over the
            // original head. The node detects the face in each refined crop and in the matching
            // source crop and shifts the refined one so the two faces coincide (translation only,
            // so a substituted identity is not distorted).
            if (autoAlignPaste)
            {
                string realignNode = g.CreateNode("SwarmH3FaceRealign", new JObject()
                {
                    ["refined_crops"] = refinedCrops,
                    ["source_crops"] = crops,
                    ["transform"] = transform,
                    ["detector"] = "bbox/face_yolov8m.pt",
                    ["confidence"] = confidence,
                    ["max_shift"] = g.UserInput.Get(H3FaceAlignMaxShift, H3FaceRefineDefaults.AlignMaxShift),
                    ["align_strength"] = g.UserInput.Get(H3FaceAlignStrength, H3FaceRefineDefaults.AlignStrength),
                    ["smooth_frames"] = g.UserInput.Get(H3FaceAlignSmooth, H3FaceRefineDefaults.AlignSmooth)
                });
                refinedCrops = [realignNode, 0];
            }

            string stitchNode = g.CreateNode("H3FaceStitch", new JObject()
            {
                ["base_images"] = frames,
                ["refined_crops"] = refinedCrops,
                ["transform"] = transform,
                ["paste_region"] = "face_ellipse",
                ["mask_dilation"] = 24,
                ["feather"] = feather,
                ["colour_match"] = 1.0,
                ["blend"] = 1.0,
                ["undetected_frames"] = "fade_out"
            });
            return [stitchNode, 0];
        }

        JArray finalFrames;
        if (targetMode == "Both Faces (Chained)")
        {
            // Stage 1: Left person (Person 1) using Reference 1 if supplied
            JArray stage1 = ProcessFaceStage(inputFrames, "left_most", 0, "Person 1 (Left)", 77, refImageNode1);
            // Stage 2: Right person (Person 2) chained onto Stage 1 output, using Reference 2 if supplied
            finalFrames = ProcessFaceStage(stage1, "right_most", 0, "Person 2 (Right)", 78, refImageNode2);
        }
        else if (targetMode == "Left Person")
        {
            finalFrames = ProcessFaceStage(inputFrames, "left_most", 0, "Left Person", 77, refImageNode1);
        }
        else if (targetMode == "Right Person")
        {
            finalFrames = ProcessFaceStage(inputFrames, "right_most", 0, "Right Person", 77, refImageNode2 ?? refImageNode1);
        }
        else if (targetMode == "Secondary Face")
        {
            finalFrames = ProcessFaceStage(inputFrames, "largest_face", 1, "Secondary Face", 77, refImageNode2 ?? refImageNode1);
        }
        else if (targetMode == "Center Person")
        {
            finalFrames = ProcessFaceStage(inputFrames, "centre_most", 0, "Center Person", 77, refImageNode1);
        }
        else if (targetMode == "Identity Match (Reference Image)" && hasRefImage1)
        {
            finalFrames = ProcessFaceStage(inputFrames, "largest_face", 0, "Identity Match (Reference)", 77, refImageNode1, true);
        }
        else // Primary Face (Largest)
        {
            finalFrames = ProcessFaceStage(inputFrames, "largest_face", 0, (refImageNode1 is not null) ? "Reference Target Face" : "Primary Face", 77, refImageNode1);
        }

        return finalFrames;
    }
}
