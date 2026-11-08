// MiniMax H3 Face Refine action button in SwarmUI history & media viewers

// addInstallButton(groupId, featureId, installId, buttonText) - 'H3 Face Refine' cleans to group ID 'hfacerefine'.
addInstallButton('hfacerefine', 'h3_face_refine', 'h3_face_refine', 'Install H3 Face Refine Nodes');
addInstallButton('hfacerefine', 'comfyui_impact_pack', 'comfyui_impact_pack', 'Install Impact Pack (SAM face masks)');

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

/** Keeps the 'H3 Face Mode' shortcut and the controls it sets in agreement. */
class H3FaceModeHelper {
    /** Sets up the values of each mode. 'Restore' must match H3FaceRefineDefaults in the C# code. */
    constructor() {
        this.modeId = 'hfacemode';
        this.manualMode = 'Manual';
        this.modes = {
            'Restore': { hfacedenoise: 0.27, hfacedenoiselargeface: 0.65, hfacerefimagesize: 'match' },
            'Substitute Features': { hfacedenoise: 0.5, hfacedenoiselargeface: 1, hfacerefimagesize: 'max' },
            'Substitute Full Identity': { hfacedenoise: 0.65, hfacedenoiselargeface: 1, hfacerefimagesize: 'max' }
        };
        this.controlIds = Object.keys(this.modes['Restore']);
    }

    /** Connects to the freshly built controls. Runs after every parameter rebuild, and rebuilt elements have no listeners. */
    connect() {
        let modeElem = document.getElementById(`input_${this.modeId}`);
        if (!modeElem || modeElem.dataset.h3FaceModeConnected) {
            return;
        }
        modeElem.dataset.h3FaceModeConnected = 'true';
        modeElem.addEventListener('change', (event) => {
            // Only a real user choice applies a mode. Swarm dispatches untrusted events when it restores or resets values.
            if (event.isTrusted) {
                this.applyMode(modeElem.value);
            }
        });
        for (let id of this.controlIds) {
            document.getElementById(`input_${id}`)?.addEventListener('change', () => this.syncMode());
        }
        this.syncMode();
    }

    /** Sets the controls of the given mode. 'Manual' has no values, so it changes nothing. */
    applyMode(mode) {
        let values = this.modes[mode];
        if (!values) {
            return;
        }
        for (let id of this.controlIds) {
            let param = gen_param_types.find(p => p.id == id);
            setDirectParamValue(param, values[id]);
        }
    }

    /** Shows the mode that matches the current control values, or 'Manual' if none does. Never changes the controls. */
    syncMode() {
        let modeElem = document.getElementById(`input_${this.modeId}`);
        if (!modeElem) {
            return;
        }
        let matched = this.manualMode;
        for (let mode of Object.keys(this.modes)) {
            if (this.valuesMatch(this.modes[mode])) {
                matched = mode;
                break;
            }
        }
        modeElem.value = matched;
    }

    /** Returns true if every control currently has the given value. */
    valuesMatch(values) {
        for (let id of this.controlIds) {
            let elem = document.getElementById(`input_${id}`);
            if (!elem) {
                return false;
            }
            let expected = values[id];
            let matches = typeof expected == 'number' ? Math.abs(parseFloat(elem.value) - expected) < 0.001 : elem.value == expected;
            if (!matches) {
                return false;
            }
        }
        return true;
    }
}

let h3FaceModeHelper = new H3FaceModeHelper();

postParamBuildSteps.push(() => {
    h3FaceModeHelper.connect();
});
