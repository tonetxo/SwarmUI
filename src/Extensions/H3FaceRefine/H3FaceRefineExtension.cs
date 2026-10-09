using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
    public static T2IRegisteredParam<int> H3FaceRefineSteps;
    public static T2IRegisteredParam<string> H3FaceRefineCanvasSize;
    public static T2IRegisteredParam<int> H3FaceRefineFeather;
    public static T2IRegisteredParam<double> H3FaceRefineConfidence;
    public static T2IRegisteredParam<string> H3FaceRefinePrompt;
    public static T2IRegisteredParam<Image> H3FaceRefineReferenceImage;

    // Internal history video parameter
    public static T2IRegisteredParam<string> H3FaceRefineVideoFile;

    public static readonly string[] TargetOptions =
    [
        "Both Faces (Chained)",
        "Primary Face (Largest)",
        "Secondary Face",
        "Left Person",
        "Right Person",
        "Center Person"
    ];

    public override void OnInit()
    {
        Logs.Init("MiniMax H3 Face Refine Extension initializing...");

        // Map ComfyUI node to feature set
        ComfyUIBackendExtension.NodeToFeatureMap["H3FaceTrackCrop"] = "h3_face_refine";

        // Register installable feature if missing
        InstallableFeatures.RegisterInstallableFeature(new(
            "MiniMax H3 Face Refine Nodes",
            "h3_face_refine",
            "https://github.com/Carasibana/ComfyUI-H3-FaceRefine",
            "comfyui",
            "This will install the ComfyUI-H3-FaceRefine node pack for tracking and restoring faces in MiniMax H3 videos.\nDo you wish to install?"
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
                         "- 'Center Person': Focuses on the central subject.",
            Default: "Both Faces (Chained)",
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true,
            GetValues: _ => TargetOptions.ToList()
        ));

        H3FaceRefineDenoise = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "H3 Face Denoise",
            Description: "Denoising strength for the face region (0.22-0.30 recommended for H3 Flow Matching).\n" +
                         "0.20-0.28 preserves head orientation, jawline, and hairline while transferring facial likeness.\n" +
                         "Values above 0.35 cause head drift relative to the body due to H3's sigma shift.",
            Default: "0.26",
            Min: 0.05,
            Max: 0.80,
            Step: 0.01,
            ViewType: ParamViewType.SLIDER,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine",
            DoNotPreview: true
        ));

        H3FaceRefineSteps = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "H3 Face Steps",
            Description: "Sampling steps for the MiniMax H3 DiT refiner. Typically 4 to 8 steps.",
            Default: "8",
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
            Description: "Gaussian blur feather radius in source pixels applied to the composite paste mask. 12-20 provides soft, invisible edge blending.",
            Default: "16",
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
            Default: "0.35",
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
            Description: "[Optional] Reference portrait/photo of the person to restore.\n" +
                         "- In H3FaceTrackCrop: Uses InsightFace to lock onto this person's identity across the entire video.\n" +
                         "- In MiniMax H3: Conditions the DiT on this reference portrait (<Picture 1>) to transfer exact facial features, likeness, and high-frequency details.",
            Default: null,
            Toggleable: true,
            Group: H3FaceRefineGroup,
            OrderPriority: priority++,
            FeatureFlag: "h3_face_refine"
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

    private static string ResolveVideoPath(User user, string videoFile)
    {
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
                videoFile = UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
            }
        }

        return videoFile;
    }

    private static (string container, string codec) GetVideoFormat(WorkflowGenerator g)
    {
        string swarmVideoFormat = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4");
        string container = "mp4";
        string codec = "auto";
        if (swarmVideoFormat.Contains("-"))
        {
            string[] parts = swarmVideoFormat.Split('-');
            codec = parts[0];
            container = parts[1];
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
            // Attempt to find any MiniMax H3 model available
            T2IModel h3Model = Program.T2IModelSets["Diffusion Models"]?.Models?.Values?.FirstOrDefault(m => m.ModelClass?.ID == T2IModelClassSorter.CompatMiniMaxH3.ID)
                ?? Program.T2IModelSets["Stable-Diffusion"]?.Models?.Values?.FirstOrDefault(m => m.ModelClass?.ID == T2IModelClassSorter.CompatMiniMaxH3.ID);

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

        byte[] rawVideoData = File.ReadAllBytes(resolvedPath);
        VideoFile sourceVideo = new(rawVideoData, MediaType.GetByExtension(resolvedPath.AfterLast('.').ToLowerFast()));

        WGNodeData videoData = g.LoadVideo(sourceVideo, "${h3_face_refine_source_video}", false);
        JArray currentFrames = videoData.Path;
        JArray videoAudio = videoData.AttachedAudio?.Path;
        JToken videoFps = videoData.FPS;

        JArray finalFrames = ExecuteFaceRefinementPipeline(g, currentFrames);

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
        JArray refinedFrames = ExecuteFaceRefinementPipeline(g, g.CurrentMedia.Path);

        // Update CurrentMedia preserving attached audio, FPS, etc.
        g.CurrentMedia = g.CurrentMedia.WithPath(refinedFrames);
        Logs.Info("H3 Face Refine: Live face refinement pass successfully connected into generation pipeline.");
    }

    /// <summary>Modular pipeline for tracking, cropping, DiT refining, and seamless face stitching.</summary>
    public static JArray ExecuteFaceRefinementPipeline(WorkflowGenerator g, JArray inputFrames)
    {
        string targetMode = g.UserInput.Get(H3FaceRefineTarget, "Both Faces (Chained)");
        double denoise = g.UserInput.Get(H3FaceRefineDenoise, 0.38);
        int steps = g.UserInput.Get(H3FaceRefineSteps, 8);
        string canvasMode = g.UserInput.Get(H3FaceRefineCanvasSize, "auto_capped_768");
        int feather = g.UserInput.Get(H3FaceRefineFeather, 16);
        double confidence = g.UserInput.Get(H3FaceRefineConfidence, 0.35);
        bool hasRefImage = g.UserInput.TryGet(H3FaceRefineReferenceImage, out Image refImg) && refImg is not null;
        string userPrompt = g.UserInput.Get(H3FaceRefinePrompt, "");
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            userPrompt = hasRefImage
                ? "photorealistic face of this person, realistic eyes, detailed facial features, natural skin texture"
                : "high quality detailed face, realistic eyes, natural skin texture";
        }

        long baseSeed = g.UserInput.Get(T2IParamTypes.Seed, 0);
        JArray refImageNode = null;
        if (hasRefImage)
        {
            refImageNode = g.LoadImage(refImg, "${h3_face_refine_reference_image}", false).Path;
            Logs.Info("H3 Face Refine: Reference portrait image loaded for InsightFace identity lock & DiT reference conditioning.");
        }

        // Helper to process one face stage
        JArray ProcessFaceStage(JArray frames, string selectMode, int selectIndex, string stageLabel, long seedOffset)
        {
            Logs.Info($"H3 Face Refine: Adding stage '{stageLabel}' (select={selectMode}, index={selectIndex}, denoise={denoise}, steps={steps}, hasRef={refImageNode is not null})");

            JObject trackCropInputs = new()
            {
                ["images"] = frames,
                ["detector"] = "bbox/face_yolov8m.pt",
                ["confidence"] = confidence,
                ["crop_factor"] = 2.5,
                ["canvas_width"] = 768,
                ["canvas_height"] = 768,
                ["canvas_mode"] = canvasMode,
                ["smooth_window"] = 11,
                ["size_smooth_window"] = 51,
                ["smooth_method"] = "gaussian",
                ["size_mode"] = "per_frame",
                ["identity_track"] = refImageNode is not null,
                ["identity_threshold"] = 0.28,
                ["select"] = selectMode,
                ["fallback_detector"] = "segm/person_yolov8m-seg.pt",
                ["fallback_head_frac"] = 0.5,
                ["select_index"] = selectIndex,
                ["identity_model"] = "insightface",
                ["cut_detection"] = "none"
            };
            if (refImageNode is not null)
            {
                trackCropInputs["identity_reference"] = refImageNode;
            }
            string trackCropNode = g.CreateNode("H3FaceTrackCrop", trackCropInputs);
            JArray crops = [trackCropNode, 0];
            JArray transform = [trackCropNode, 1];
            JArray canvasW = [trackCropNode, 4];
            JArray canvasH = [trackCropNode, 5];
            JArray frameCount = [trackCropNode, 6];

            string stagePrompt = userPrompt;
            if (refImageNode is not null && !stagePrompt.Contains("<Picture 1>"))
            {
                stagePrompt = $"<Picture 1> {stagePrompt}";
            }

            JObject ref2vidInputs = new()
            {
                ["clip"] = g.CurrentTextEnc.Path,
                ["prompt"] = stagePrompt,
                ["width"] = canvasW,
                ["height"] = canvasH,
                ["length"] = frameCount,
                ["ref_image_size"] = "match",
                ["vae"] = g.CurrentVae.Path
            };
            if (refImageNode is not null)
            {
                ref2vidInputs["ref_image_1"] = refImageNode;
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

            JObject perFrameInputs = new()
            {
                ["model"] = g.CurrentModel.Path,
                ["av_latent"] = injectedLatent,
                ["transform"] = transform,
                ["denoise_multiplier_small_face"] = 1.0,
                ["denoise_multiplier_large_face"] = 1.0,
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

            string stitchNode = g.CreateNode("H3FaceStitch", new JObject()
            {
                ["base_images"] = frames,
                ["refined_crops"] = refinedCrops,
                ["transform"] = transform,
                ["paste_region"] = "face_ellipse",
                ["mask_dilation"] = 6,
                ["feather"] = feather,
                ["colour_match"] = 0.5,
                ["blend"] = 1.0,
                ["undetected_frames"] = "fade_out"
            });
            return [stitchNode, 0];
        }

        JArray finalFrames;
        if (targetMode == "Both Faces (Chained)" && !hasRefImage)
        {
            // Stage 1: Left person
            JArray stage1 = ProcessFaceStage(inputFrames, "left_most", 0, "Person 1 (Left)", 77);
            // Stage 2: Right person (chained onto stage 1 output)
            finalFrames = ProcessFaceStage(stage1, "right_most", 0, "Person 2 (Right)", 78);
        }
        else if (targetMode == "Left Person")
        {
            finalFrames = ProcessFaceStage(inputFrames, "left_most", 0, "Left Person", 77);
        }
        else if (targetMode == "Right Person")
        {
            finalFrames = ProcessFaceStage(inputFrames, "right_most", 0, "Right Person", 77);
        }
        else if (targetMode == "Secondary Face")
        {
            finalFrames = ProcessFaceStage(inputFrames, "largest_face", 1, "Secondary Face", 77);
        }
        else if (targetMode == "Center Person")
        {
            finalFrames = ProcessFaceStage(inputFrames, "centre_most", 0, "Center Person", 77);
        }
        else // Primary Face (Largest) or reference target
        {
            finalFrames = ProcessFaceStage(inputFrames, "largest_face", 0, hasRefImage ? "Reference Target Face" : "Primary Face", 77);
        }

        return finalFrames;
    }
}
