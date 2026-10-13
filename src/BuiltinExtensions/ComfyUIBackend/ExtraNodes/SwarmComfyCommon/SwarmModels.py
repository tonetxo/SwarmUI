import folder_paths, torch, comfy, math
import comfy.utils, comfy.sd, comfy.audio, nodes, node_helpers
from comfy_api.latest import io

MiniMaxReferences = io.Custom("MiniMaxReferences")

class SwarmLTXVAudioVAELoader(io.ComfyNode):
    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmLTXVAudioVAELoader",
            display_name="Swarm LTXV Audio VAE Loader",
            category="SwarmUI/video",
            description="Loads an LTX-2 audio VAE from the VAE models folder.",
            inputs=[
                io.Combo.Input("vae_name", options=folder_paths.get_filename_list("vae"), tooltip="Audio VAE file."),
            ],
            outputs=[io.Vae.Output(display_name="Audio VAE")],
        )

    @classmethod
    def execute(cls, vae_name: str) -> io.NodeOutput:
        vae_path = folder_paths.get_full_path_or_raise("vae", vae_name)
        sd, metadata = comfy.utils.load_torch_file(vae_path, return_metadata=True)
        sd = comfy.utils.state_dict_prefix_replace(sd, {"audio_vae.": "autoencoder."})
        vae = comfy.sd.VAE(sd=sd, metadata=metadata)
        vae.throw_exception_if_invalid()
        return io.NodeOutput(vae)

def _resize(image, width, height, crop):
    # image [B, H, W, C] -> [B, height, width, 3]
    samples = image[..., :3].movedim(-1, 1)
    samples = comfy.utils.common_upscale(samples, width, height, "lanczos", crop)
    return samples.movedim(1, -1)

class SwarmMiniMaxH3AddKeyframes(io.ComfyNode):
    """Adds keyframes to MiniMax H3 format."""

    @classmethod
    def define_schema(cls):
        return io.Schema(
            node_id="SwarmMiniMaxH3AddKeyframes",
            display_name="Swarm MiniMax H3 Add Keyframes",
            category="SwarmUI/video",
            inputs=[
                io.Vae.Input("vae"),
                io.Latent.Input("latent"),
                io.Conditioning.Input("conditioning"),
                io.Image.Input("first_frame", optional=True),
                io.Image.Input("last_frame", optional=True),
            ],
            outputs=[io.Conditioning.Output(display_name="conditioning"),],
        )

    @classmethod
    def execute(cls, vae, latent, conditioning, first_frame=None, last_frame=None) -> io.NodeOutput:
        latent = latent["samples"].unbind()[0]
        frame_count = 5 + 17 * ((latent.shape[2] - 2) // 5)
        width = latent.shape[4] * 16
        height = latent.shape[3] * 16

        images = []
        keyframes = []
        if first_frame is not None:
            img = _resize(first_frame[:1], width, height, "disabled")
            images.append(img)
            keyframes.append({"resolved_frame_index": 0, "image": img})
        if last_frame is not None:
            img = _resize(last_frame[:1], width, height, "disabled")
            images.append(img)
            keyframes.append({"resolved_frame_index": frame_count - 1, "image": img})

        if keyframes:
            for kf in keyframes:
                kf["latent"] = vae.encode(kf.pop("image"))
            conditioning = node_helpers.conditioning_set_values(conditioning, {
                "minimax_keyframes": keyframes,
                "minimax_frame_count": frame_count,
            })
        return io.NodeOutput(conditioning,)

########## MiniMax H3 direct rips - basically all the below is straight from comfy nodes_minimax_h3.py, but tweaked to be more useful ##########

def align_frame_count(n):
    while n % 17 != 5:
        n += 1
    return n

def video_latent_t(frame_count):
    return 2 if frame_count <= 5 else ((frame_count - 5) // 17) * 5 + 2

H3_FPS = 24
H3_AUDIO_LATENT_FPS = 40
H3_CANVAS_MULTIPLE = 32
H3_BASE_SHORT_EDGE = 768
H3_MAX_PIXELS = 768 * 1344
H3_REF_IMAGE_SHORT_EDGE = 2048

def temporal_shape(length):
    if length == 1:
        return 1, 1, 2
    elif length == 2:
        return 2, 2, 3
    frame_count = align_frame_count(max(5, length))
    duration = frame_count / H3_FPS
    return frame_count, video_latent_t(frame_count), round(duration * H3_AUDIO_LATENT_FPS)

def _empty_av_latent(width, height, length, batch_size=1):
    frame_count, latent_t, audio_t = temporal_shape(length)
    video = torch.zeros([batch_size, 24, latent_t, height // 16, width // 16],
                        device=comfy.model_management.intermediate_device())
    audio = torch.zeros([batch_size, 32, 2, audio_t],
                        device=comfy.model_management.intermediate_device())
    return {"samples": comfy.nested_tensor.NestedTensor((video, audio))}, frame_count

class SwarmEmptyMiniMaxH3LatentAV(io.ComfyNode):
    @classmethod
    def define_schema(cls):
        return io.Schema(
            node_id="SwarmEmptyMiniMaxH3LatentAV",
            display_name="Swarm Empty MiniMax H3 AV Latent",
            category="SwarmUI/video",
            description="Joint video+audio latent for MiniMax H3.",
            inputs=[
                io.Int.Input("width", default=1344, min=32, max=nodes.MAX_RESOLUTION, step=32),
                io.Int.Input("height", default=768, min=32, max=nodes.MAX_RESOLUTION, step=32),
                io.Int.Input("length", default=124, min=1, max=3600, step=1, tooltip="Frame count at 24 fps, snapped up to the model's 17k+5 grid (124 = ~5s; trained range is ~124-362, longer is untested)"),
            ],
            outputs=[io.Latent.Output()],
        )

    @classmethod
    def execute(cls, width, height, length) -> io.NodeOutput:
        latent, _ = _empty_av_latent(width, height, length)
        return io.NodeOutput(latent)

def _adapt_canvas(width, height):
    ratio = width / height
    if ratio >= 1.0:
        nom_w, nom_h = H3_BASE_SHORT_EDGE * ratio, H3_BASE_SHORT_EDGE
    else:
        nom_w, nom_h = H3_BASE_SHORT_EDGE, H3_BASE_SHORT_EDGE / ratio
    if nom_w * nom_h > H3_MAX_PIXELS:
        s = math.sqrt(H3_MAX_PIXELS / (nom_w * nom_h))
        nom_w, nom_h = nom_w * s, nom_h * s
    return (max(H3_CANVAS_MULTIPLE, round(nom_w / H3_CANVAS_MULTIPLE) * H3_CANVAS_MULTIPLE),
            max(H3_CANVAS_MULTIPLE, round(nom_h / H3_CANVAS_MULTIPLE) * H3_CANVAS_MULTIPLE))

def _encode_ref_audio(audio_vae, audio):
    waveform = audio["waveform"]
    sr = audio["sample_rate"]
    vae_sr = getattr(audio_vae, "audio_sample_rate", 32000)
    if sr != vae_sr:
        waveform = comfy.audio.resample(waveform, sr, vae_sr)
    z = audio_vae.encode(waveform[:1].movedim(1, -1))
    return z, z.shape[-1]

class SwarmMiniMaxH3CollectReferences(io.ComfyNode):
    """Collects MiniMax H3 reference images / videos / audio into MiniMaxReferences."""

    @classmethod
    def define_schema(cls):
        return io.Schema(
            node_id="SwarmMiniMaxH3CollectReferences",
            display_name="Swarm MiniMax H3 Collect References",
            category="SwarmUI/video",
            description="<Picture i> / <Video k> / <Audio j> reference collection for MiniMax H3. Feed the output into SwarmTextEncodeAdvanced.",
            inputs=[
                io.Vae.Input("vae"),
                io.Vae.Input("audio_vae"),
                io.Int.Input("width", default=1344, min=32, max=nodes.MAX_RESOLUTION, step=32),
                io.Int.Input("height", default=768, min=32, max=nodes.MAX_RESOLUTION, step=32),
                io.Int.Input("length", default=124, min=1, max=3600, step=1, tooltip="Frame count at 24 fps, used to crop reference videos to the generation length."),
                io.Combo.Input("ref_image_size", options=["match", "max"], default="match", tooltip="Reference image sizing. 'match' scales each ref (down only, keeping aspect) to the generation's pixel area; 'max' uses the reference pipeline's 2048px short edge for best identity fidelity."),
                io.Autogrow.Input("ref_images", optional=True, template=io.Autogrow.TemplatePrefix(input=io.Image.Input("ref_image", tooltip="Reference image (downscaled to 2048 short edge if larger, never upscaled)"), prefix="ref_image_", min=0, max=9)),
                io.Autogrow.Input("ref_videos", optional=True, template=io.Autogrow.TemplatePrefix(input=io.Image.Input("ref_video", tooltip="Reference video frames at 24 fps (2-15s)"), prefix="ref_video_", min=0, max=3)),
                io.Autogrow.Input("ref_video_audios", optional=True, template=io.Autogrow.TemplatePrefix(input=io.Audio.Input("ref_video_audio", tooltip="Soundtrack of the same-numbered reference video"), prefix="ref_video_audio_", min=0, max=3)),
                io.Autogrow.Input("ref_audios", optional=True, template=io.Autogrow.TemplatePrefix(input=io.Audio.Input("ref_audio", tooltip="Standalone reference audio"), prefix="ref_audio_", min=0, max=3)),
            ],
            outputs=[MiniMaxReferences.Output(display_name="MiniMax References")],
        )

    @classmethod
    def execute(cls, vae, audio_vae, width, height, length, ref_image_size="match",
                ref_images=None, ref_videos=None, ref_video_audios=None, ref_audios=None) -> io.NodeOutput:
        frame_count, _, _ = temporal_shape(length)
        ref_items = []
        ref_blocks = []

        for img in (ref_images or {}).values():
            if img is None:
                continue
            h, w = img.shape[1], img.shape[2]
            if ref_image_size == "match":
                scale = min(1.0, math.sqrt((width * height) / (w * h)))
            else:
                scale = min(1.0, H3_REF_IMAGE_SHORT_EDGE / min(w, h))
            tw = max(H3_CANVAS_MULTIPLE, round(w * scale / H3_CANVAS_MULTIPLE) * H3_CANVAS_MULTIPLE)
            th = max(H3_CANVAS_MULTIPLE, round(h * scale / H3_CANVAS_MULTIPLE) * H3_CANVAS_MULTIPLE)
            resized = _resize(img[:1], tw, th, "disabled")
            z = vae.encode(resized)
            ref_items.append({"type": "image", "data": resized})
            ref_blocks.append({"kind": "image", "latent_h": th // 16, "latent_w": tw // 16, "latent": z})

        ref_video_audios = ref_video_audios or {}
        for name, video_frames in (ref_videos or {}).items():
            if video_frames is None:
                continue
            soundtrack = ref_video_audios.get("ref_video_audio_" + name.rsplit("_", 1)[-1])
            vh, vw = video_frames.shape[1], video_frames.shape[2]
            cw, ch = _adapt_canvas(vw, vh)
            if vw * vh < cw * ch:
                cw = max(H3_CANVAS_MULTIPLE, round(vw / H3_CANVAS_MULTIPLE) * H3_CANVAS_MULTIPLE)
                ch = max(H3_CANVAS_MULTIPLE, round(vh / H3_CANVAS_MULTIPLE) * H3_CANVAS_MULTIPLE)
            frames = _resize(video_frames, cw, ch, "disabled")
            if frames.shape[0] > frame_count:
                frames = frames[:frame_count]
            n = frames.shape[0]
            if n < 5:
                raise ValueError("MiniMax H3 reference videos need at least 5 frames (~0.2s at 24 fps)")
            while n % 17 != 5:
                n -= 1
            frames = frames[:n]
            z = vae.encode(frames)
            audio_latent, ref_audio_t = (None, 0)
            if soundtrack is not None:
                audio_latent, ref_audio_t = _encode_ref_audio(audio_vae, soundtrack)
                ref_items.append({"type": "audio"})
            sample_idx = list(range(0, frames.shape[0], H3_FPS // 2))
            qwen_frames = frames[sample_idx]
            ref_items.append({"type": "video", "data": qwen_frames,
                              "timestamps": [i / 2.0 for i in range(len(sample_idx))]})
            ref_blocks.append({"kind": "video_audio" if ref_audio_t else "video",
                               "latent_t": z.shape[2], "latent_h": ch // 16, "latent_w": cw // 16,
                               "ref_audio_t": ref_audio_t, "latent": z, "audio_latent": audio_latent})

        for audio in (ref_audios or {}).values():
            if audio is None:
                continue
            audio_latent, ref_audio_t = _encode_ref_audio(audio_vae, audio)
            ref_items.append({"type": "audio"})
            ref_blocks.append({"kind": "audio", "ref_audio_t": ref_audio_t, "audio_latent": audio_latent})

        return io.NodeOutput({"ref_items": ref_items, "ref_blocks": ref_blocks})

def _realign_detector_list() -> list:
    names = []
    for key in ("ultralytics_bbox", "ultralytics"):
        try:
            names.extend(folder_paths.get_filename_list(key))
        except Exception:
            pass
    seen, out = set(), []
    for n in names:
        if n not in seen:
            seen.add(n)
            out.append(n)
    return out or ["bbox/face_yolov8m.pt"]

_REALIGN_DETECTOR_CACHE: dict = {}

def _realign_detector(name: str):
    """Ultralytics YOLO face detector, cached. None if the model file is unavailable."""
    if name in _REALIGN_DETECTOR_CACHE:
        return _REALIGN_DETECTOR_CACHE[name]
    path = None
    for key in ("ultralytics_bbox", "ultralytics"):
        try:
            path = folder_paths.get_full_path(key, name)
        except Exception:
            path = None
        if path:
            break
    if path is None:
        import os
        base = getattr(folder_paths, "models_dir", "models")
        for sub in ("ultralytics/bbox", "ultralytics", "ultralytics/segm"):
            cand = os.path.join(base, *sub.split("/"), name)
            if os.path.exists(cand):
                path = cand
                break
    if path is None:
        return None
    from ultralytics import YOLO
    model = YOLO(path)
    _REALIGN_DETECTOR_CACHE[name] = model
    return model

_REALIGN_FACEAPP: dict = {}

def _realign_face_app():
    """InsightFace detection model (SCRFD, 5-point landmarks incl. both eyes), cached.

    Runs on CPU deliberately: the crops are small single-frame images (512-640 px) so
    inference is fast enough, and keeping it off CUDA avoids cuDNN/ORT symbol conflicts
    while the main diffusion model is loaded. Eye landmarks, not the bounding box:
    a repainted/substituted face changes the box shape (beard/jaw) without moving the
    eyes, so aligning on boxes pushes the face away from the source.
    """
    if "app" not in _REALIGN_FACEAPP:
        app = None
        try:
            from insightface.app import FaceAnalysis
            app = FaceAnalysis(name="buffalo_l", allowed_modules=["detection"],
                               providers=["CPUExecutionProvider"])
            app.prepare(ctx_id=-1, det_size=(640, 640))
        except Exception as exc:
            print(f"[SwarmH3FaceRealign] insightface unavailable ({exc}) - falling back to YOLO box centres")
            app = None
        _REALIGN_FACEAPP["app"] = app
    return _REALIGN_FACEAPP["app"]

class SwarmH3FaceRealign(io.ComfyNode):
    """Shifts each refined face crop so the eyes inside it land exactly on the source eyes.

    H3's regeneration re-frames the face content inside the crop (measured on the target clip:
    the paste position is exact, but the face inside the crop drifts a few pixels on some
    frames), which reads in the composite as the refined face sliding over the original head.
    This detects the EYE MIDPOINT (insightface landmarks) in the source crop and in the matching
    generated crop and translates the generated one so the two coincide. Translation only, so a
    substituted identity is never distorted; boxes are deliberately not used, since a beard or a
    different jaw changes the box shape without moving the eyes.
    """

    @classmethod
    def define_schema(cls):
        return io.Schema(
            node_id="SwarmH3FaceRealign",
            display_name="Swarm H3 Face Realign",
            category="SwarmUI/video",
            description="Detects the eye midpoint in each source crop and in the matching refined crop, then shifts the refined crop so the two coincide, cancelling the content offset that makes the pasted face visibly slide. Translation (+optional uniform scale) anchored on eye landmarks so a substituted identity is not distorted.",
            inputs=[
                io.Image.Input("refined_crops"),
                io.Image.Input("source_crops"),
                io.Custom("H3FACEXFORM").Input("transform"),
                io.Combo.Input("detector", options=_realign_detector_list(), default="bbox/face_yolov8m.pt", tooltip="YOLO face detector, used only as a fallback anchor when insightface is unavailable."),
                io.Float.Input("confidence", default=0.35, min=0.05, max=1.0, step=0.05),
                io.Float.Input("max_shift", default=40.0, min=1.0, max=200.0, step=1.0, tooltip="Skip the shift on frames whose two detected faces are farther than this (pixels, canvas space) apart - those are mismatched detections or tracking breakage, not a small offset."),
                io.Float.Input("align_strength", default=1.0, min=0.0, max=1.0, step=0.05),
                io.Int.Input("smooth_frames", default=9, min=1, max=51, step=2, tooltip="Gaussian window used to smooth the shift over time within each shot."),
                io.Boolean.Input("scale_match", default=True, tooltip="Also match the inter-eye distance (uniform scale, clamped 0.75-1.35), cancelling H3's progressive zoom of the repainted face inside the crop."),
            ],
            outputs=[io.Image.Output(display_name="realigned_crops")],
        )

    @classmethod
    def execute(cls, refined_crops, source_crops, transform, detector: str, confidence: float,
                max_shift: float, align_strength: float, smooth_frames: int, scale_match: bool = True) -> io.NodeOutput:
        import numpy as np
        import comfy.model_management as mm
        total_frames = min(refined_crops.shape[0], source_crops.shape[0])
        if total_frames < 1:
            return io.NodeOutput(refined_crops)
        app = _realign_face_app()
        box_model = _realign_detector(detector)
        if app is None and box_model is None:
            print(f"[SwarmH3FaceRealign] no face detector available - passing through unchanged")
            return io.NodeOutput(refined_crops)
        dev = mm.get_torch_device()
        detected = transform.get("detected") or [True] * total_frames
        absent = transform.get("absent") or [False] * total_frames
        # The tracker may auto-pad the clip to its 17k+5 frame grid and the stitch trims the
        # padding back after us - so the transform lists (real frames) can be SHORTER than the
        # crop batches. Only realign the frames the transform describes; padding passes through.
        k_count = min(total_frames, len(detected), len(absent))
        if k_count < total_frames:
            print(f"[SwarmH3FaceRealign] transform covers {k_count}/{total_frames} frame(s) - "
                  f"the trailing {total_frames - k_count} padded frame(s) pass through unaligned")
        ref = refined_crops[:k_count, ..., :3].to(dev).float()
        src = source_crops[:k_count, ..., :3].to(dev).float()
        ch, cw = int(ref.shape[1]), int(ref.shape[2])
        # Source and refined crops should share the canvas size; scale source centres if not.
        sx_scale = cw / max(int(src.shape[2]), 1)
        sy_scale = ch / max(int(src.shape[1]), 1)
        segs = [(int(a), int(b)) for a, b in (transform.get("segments") or [(0, k_count)])]

        def anchor(t):
            centres = np.zeros((k_count, 2), np.float64)
            eyedist = np.zeros(k_count, np.float64)
            ok = np.zeros(k_count, bool)
            for k in range(k_count):
                if not detected[k] or absent[k]:
                    continue
                bgr = (t[k].clamp(0, 1).cpu().numpy() * 255.0).astype(np.uint8)[..., ::-1].copy()
                if app is not None:
                    faces = app.get(bgr)
                    if faces:
                        f = max(faces, key=lambda x: (x.bbox[2] - x.bbox[0]) * (x.bbox[3] - x.bbox[1]))
                        kps = f.kps
                        if kps is not None and len(kps) >= 2:
                            centres[k] = ((kps[0][0] + kps[1][0]) / 2.0, (kps[0][1] + kps[1][1]) / 2.0)
                            eyedist[k] = math.hypot(kps[1][0] - kps[0][0], kps[1][1] - kps[0][1])
                            ok[k] = True
                            continue
                if box_model is None:
                    continue
                res = box_model.predict(bgr, conf=float(confidence), verbose=False)[0]
                boxes = list(res.boxes)
                if not boxes:
                    continue
                b = max(boxes, key=lambda x: (x.xyxy[0][2] - x.xyxy[0][0]) * (x.xyxy[0][3] - x.xyxy[0][1]))
                x0, y0, x1, y1 = [float(v) for v in b.xyxy[0].tolist()]
                centres[k] = ((x0 + x1) / 2.0, (y0 + y1) / 2.0)
                eyedist[k] = math.hypot(x1 - x0, y1 - y0) * 0.35
                ok[k] = True
            return centres, eyedist, ok

        src_c, src_d, src_ok = anchor(src)
        ref_c, ref_d, ref_ok = anchor(ref)
        both_ok = src_ok & ref_ok

        # Shift that moves the refined face centre onto the source face centre.
        shift = np.zeros((k_count, 2), np.float64)
        good = np.zeros(k_count, bool)
        for k in range(k_count):
            if not both_ok[k]:
                continue
            ox = src_c[k, 0] * sx_scale - ref_c[k, 0]
            oy = src_c[k, 1] * sy_scale - ref_c[k, 1]
            if math.hypot(ox, oy) > max_shift:
                continue
            shift[k] = (ox, oy)
            good[k] = True
        if not good.any():
            print(f"[SwarmH3FaceRealign] no frame had both faces within {max_shift:.0f}px "
                  f"(source {int(src_ok.sum())}, refined {int(ref_ok.sum())}, both {int(both_ok.sum())}) - passing through unchanged")
            return io.NodeOutput(refined_crops)

        # Median-filter raw shifts per segment, then reject spikes: eye-landmark noise on
        # partial/profile/substituted faces creates isolated frames whose centre jumps many
        # pixels; these spikes SURVIVE gaussian smoothing (they leak into the kernel) and read
        # as the face wobbling frame to frame. A local median snaps them back. Two passes:
        # a wide-window pass first (isolated spikes), then a narrow pass (small residue).
        for window_half, threshold in ((4, 4.0), (2, 2.5)):
            med = np.zeros((k_count, 2), np.float64)
            for a0, b0 in segs:
                a = max(0, a0)
                b = min(k_count, b0)
                for k in range(a, b):
                    lo, hi = max(a, k - window_half), min(b, k + window_half + 1)
                    vals = shift[lo:hi][good[lo:hi]]
                    if vals.shape[0]:
                        med[k] = np.median(vals, axis=0)
            spikes = 0
            for k in range(k_count):
                if good[k] and math.hypot(shift[k, 0] - med[k, 0], shift[k, 1] - med[k, 1]) > threshold:
                    shift[k] = med[k]
                    spikes += 1
            if spikes:
                print(f"[SwarmH3FaceRealign] median filter (window +/-{window_half}): snapped {spikes} outlier frame(s) (>{threshold}px deviation)")

        # Fill gaps by linear interpolation and smooth over time, within each shot.
        half = int(smooth_frames) // 2
        kern = None
        if half >= 1:
            kern = np.exp(-0.5 * (np.arange(-half, half + 1) / (half / 2.0)) ** 2)
            kern /= kern.sum()

        def interp_smooth(series, fill_value, kern_use=None):
            arr = np.asarray(series, np.float64)
            arr = np.atleast_2d(arr.T).T if arr.ndim > 1 else arr[:, None]
            n_cols = arr.shape[1]
            out_v = np.full((k_count, n_cols), float(fill_value), np.float64)
            k_use = kern_use if kern_use is not None else kern
            s_half = (len(k_use) - 1) // 2 if k_use is not None else 0
            for col in range(n_cols):
                for a0, b0 in segs:
                    a = max(0, a0)
                    b = min(k_count, b0)
                    seg_v = arr[a:b, col].copy()
                    g = good[a:b]
                    span = np.arange(a, b)
                    if g.any():
                        seg_v = np.interp(span, span[g], seg_v[g])
                    else:
                        seg_v[:] = fill_value
                    if k_use is not None and len(seg_v) > s_half:
                        seg_v = np.convolve(np.pad(seg_v, s_half, mode="edge"), k_use, mode="valid")
                    out_v[a:b, col] = seg_v
            return out_v if n_cols > 1 else out_v[:, 0]

        filled = interp_smooth(shift, 0.0)

        # Scale match: H3's repaint can also progressively ZOOM the face content inside the
        # crop, which translation alone cannot cancel (reads as the pasted face slowly drifting
        # off the head). The inter-eye distance gives the scale; undoing it is uniform scaling,
        # so the substituted identity's shape is not distorted.
        if scale_match:
            min_d = 6.0
            clip_lo, clip_hi = 0.75, 1.35
            scale = np.ones(k_count, np.float64)
            for k in range(k_count):
                if both_ok[k] and src_d[k] > min_d and ref_d[k] > min_d:
                    scale[k] = (src_d[k] * ((sx_scale + sy_scale) / 2.0)) / ref_d[k]
            for window_half, rel_thr in ((4, 0.06), (2, 0.04)):
                med_s = np.zeros(k_count, np.float64)
                for a0, b0 in segs:
                    a = max(0, a0)
                    b = min(k_count, b0)
                    for k in range(a, b):
                        lo, hi = max(a, k - window_half), min(b, k + window_half + 1)
                        vals = scale[lo:hi][good[lo:hi]]
                        if vals.shape[0]:
                            med_s[k] = np.median(vals)
                snapped_s = 0
                for k in range(k_count):
                    if good[k] and med_s[k] > 0 and abs(scale[k] - med_s[k]) / med_s[k] > rel_thr:
                        scale[k] = med_s[k]
                        snapped_s += 1
                if snapped_s:
                    print(f"[SwarmH3FaceRealign] scale median (window +/-{window_half}): snapped {snapped_s}")
            # Scale drifts SLOWLY (identity anatomy + progressive zoom). A wide window keeps the
            # low-frequency correction but stops the scale from chasing head-pose-driven eye-
            # distance changes, which read as the whole nose-to-neck zone breathing inside a
            # near-static paste mask.
            s_half_scale = max(int(smooth_frames) // 2, 20)
            kern_s = np.exp(-0.5 * (np.arange(-s_half_scale, s_half_scale + 1) / (s_half_scale / 2.0)) ** 2)
            kern_s /= kern_s.sum()
            s_all = np.clip(interp_smooth(np.where(good, scale, 1.0), 1.0, kern_use=kern_s), clip_lo, clip_hi)
            applied = [v for v, g in zip(s_all, good) if g]
            if applied:
                print(f"[SwarmH3FaceRealign] scale match: mean {np.mean(applied):.3f}  max {np.max(applied):.3f}  min {np.min(applied):.3f}")
        else:
            s_all = np.ones(k_count, np.float64)

        strength = float(max(0.0, min(1.0, align_strength)))
        ox_all = strength * filled[:, 0]
        oy_all = strength * filled[:, 1]
        s_out = 1.0 + strength * (s_all - 1.0)

        # Anchor on SMOOTHED eye tracks, never the raw per-frame centres: the affine maps the
        # refined eye midpoint onto the source one, so raw landmark noise would bypass the
        # temporal smoothing and jitter. With s=1 this reduces exactly to the old translation
        # q = p - shift_smoothed.
        src_c_sm = np.stack([interp_smooth(src_c[:, 0], 0.0), interp_smooth(src_c[:, 1], 0.0)], axis=1)
        ref_c_sm = np.stack([interp_smooth(ref_c[:, 0], 0.0), interp_smooth(ref_c[:, 1], 0.0)], axis=1)

        import torch.nn.functional as F
        out = refined_crops.clone()
        out = out[:total_frames, ..., :3] if out.shape[0] > total_frames else out
        out = out.to(dev)
        chunk = max(1, min(32, int(1024 / max((ch * cw * 3 * 4) / 2 ** 20, 1e-6))))
        for c0 in range(0, k_count, chunk):
            c1 = min(c0 + chunk, k_count)
            n = c1 - c0
            thetas = torch.zeros(n, 2, 3, device=dev, dtype=torch.float32)
            for i in range(c0, c1):
                # Pixel-space mapping (identity blended into the correction by align_strength):
                #   q = a * p + b,  a = st/s + (1 - st),  b = st * (e_r - e_s/s)
                # Unnormalized for align_corners=False via x = ((x_n + 1) * W - 1) / 2, so
                #   q_n = a * p_n + (2 b + 1 - a) / W + a - 1  per axis.
                # With s=1 and b = -shift this reduces to the old plain translation.
                st = strength
                inv_s = 1.0 / max(float(s_out[i]), 1e-3)
                a_c = st * inv_s + (1.0 - st)
                e_sx = src_c_sm[i, 0] * sx_scale
                e_sy = src_c_sm[i, 1] * sy_scale
                b_x = st * (ref_c_sm[i, 0] - e_sx / s_out[i])
                b_y = st * (ref_c_sm[i, 1] - e_sy / s_out[i])
                thetas[i - c0, 0, 0] = a_c
                thetas[i - c0, 1, 1] = a_c
                thetas[i - c0, 0, 2] = (2.0 * b_x + 1.0 - a_c) / cw + a_c - 1.0
                thetas[i - c0, 1, 2] = (2.0 * b_y + 1.0 - a_c) / ch + a_c - 1.0
            grid = F.affine_grid(thetas, (n, 3, ch, cw), align_corners=False)
            out[c0:c1] = F.grid_sample(ref[c0:c1].movedim(-1, 1), grid, mode="bilinear",
                                       padding_mode="border", align_corners=False).movedim(1, -1)

        residual = np.hypot(ox_all, oy_all)
        jx = np.hypot(np.diff(ox_all), np.diff(oy_all))
        jit_frame = -1
        if len(jx):
            jit_frame = int(jx.argmax())
        print(f"[SwarmH3FaceRealign] realigned {int(good.sum())}/{k_count} frame(s); "
              f"mean shift {residual[good].mean():.2f}px  max {residual.max():.2f}px  "
              f"[frame {int(residual.argmax())}]{'' if residual.max() <= 1.0 else '  <- largest single correction; >feather px shifts poke content outside the paste mask'}")
        if len(jx):
            print(f"[SwarmH3FaceRealign] jitter: mean {jx.mean():.2f}px/frame  max {jx.max():.2f}px/frame "
                  f"[frame {jit_frame}]"
                  f"{'' if jx.max() <= 0.35 else '  <- frame-to-frame correction variation; values >~0.35px/frame read as the face wobbling'}")
        if residual.max() > 1.0:
            runs = []
            start = None
            for k in range(k_count):
                big = residual[k] > 1.0
                if big and start is None:
                    start = k
                elif not big and start is not None:
                    runs.append((start, k - 1))
                    start = None
            if start is not None:
                runs.append((start, k_count - 1))
            print(f"[SwarmH3FaceRealign] correction runs >1px: {runs[:8]}")
        return io.NodeOutput(out)

NODE_CLASS_MAPPINGS = {
    "SwarmLTXVAudioVAELoader": SwarmLTXVAudioVAELoader,
    "SwarmMiniMaxH3AddKeyframes": SwarmMiniMaxH3AddKeyframes,
    "SwarmEmptyMiniMaxH3LatentAV": SwarmEmptyMiniMaxH3LatentAV,
    "SwarmMiniMaxH3CollectReferences": SwarmMiniMaxH3CollectReferences,
    "SwarmH3FaceRealign": SwarmH3FaceRealign,
}
