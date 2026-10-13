// MiniMax H3 Face Refine action button in SwarmUI history & media viewers
(function() {
    try {
        if (typeof deleteCookie === 'function') {
            deleteCookie('lastparam_input_hfacerefinevideofile');
        }
        let el = document.getElementById('input_hfacerefinevideofile');
        if (el) el.value = '';
    } catch (e) {
        console.error('Error cleaning H3 Face Refine param state:', e);
    }

    if (typeof registerMediaButton !== 'function') {
        console.warn('H3 Face Refine: registerMediaButton is not available');
        return;
    }

    // Precompiled pattern: matches a known video extension at the very end of the URL,
    // tolerating an optional query string. Avoids re-splitting/parsing the src string on
    // every media-button visibility check (this runs for every element in the history view).
    let videoExtensionPattern = /\.(mp4|webm|gif|mov|avi|mkv)(\?[^]*)?$/i;

    function isVideoSrc(src) {
        if (src.startsWith('data:') && src.toLowerCase().startsWith('data:video/')) {
            return false;
        }
        if (typeof getMediaType === 'function' && getMediaType(src) === 'video') {
            return true;
        }
        return videoExtensionPattern.test(src);
    }

    registerMediaButton(
        'H3 Face Refine',
        (src) => {
            console.log('[H3 Face Refine] action called, src =', src,
                '| frontend feature set:', typeof currentBackendFeatureSet === 'undefined' ? '<undefined>' : currentBackendFeatureSet,
                '| handler:', typeof mainGenHandler === 'undefined' ? '<undefined>' : typeof mainGenHandler.doGenerate);
            if (typeof currentBackendFeatureSet === 'undefined' || !currentBackendFeatureSet.includes('h3_face_refine')) {
                let message = 'H3 Face Refine is not available on the current backend. Please verify ComfyUI-H3-FaceRefine is installed.';
                console.warn('H3 Face Refine: ' + message);
                if (typeof showToast === 'function') {
                    showToast(message);
                }
                return;
            }

            if (!isVideoSrc(src)) {
                if (typeof showToast === 'function') {
                    showToast('H3 Face Refine only operates on video files.');
                }
                return;
            }

    let input_overrides = { 'images': 1 };
    let fullsrc = typeof getImageFullSrc === 'function' ? getImageFullSrc(src) : src;
    let prefix = typeof getImageOutPrefix === 'function' ? getImageOutPrefix() : 'Output';
    // SwarmUI's CleanTypeName lowercases the param name and keeps ONLY lowercase letters (digits
    // are dropped): 'H3 Face Refine Video File' -> hfacerefinevideofile. The server applies the
    // same cleaning to incoming keys, so the legacy id is what actually matches. Other H3 params
    // (denoise, steps, feather, ...) need NO overrides here: the gen handler already collects them
    // from the generate panel under their real ids, and duplicate-key overrides were winning over
    // the user's panel values (they resolve to the same id server-side, and landed last).
    input_overrides['hfacerefinevideofile'] = prefix + '/' + fullsrc;

    // Disarm standard refiners / init images from the session so they cannot contaminate on-demand video refinement
    input_overrides['refinermethod'] = null;
    input_overrides['refinermodel'] = null;
    input_overrides['refinerupscale'] = null;
    input_overrides['initimage'] = null;

            if (typeof showToast === 'function') {
                showToast('Starting H3 Face Refine on video...');
            }

            if (typeof mainGenHandler !== 'undefined' && mainGenHandler.doGenerate) {
                mainGenHandler.doGenerate(input_overrides, {});
            }
        },
        'Refine and restore faces in this video using MiniMax H3 Face Refiner (supports 1 or 2 faces)',
        ['video'],
        true,
        true
    );
})();
