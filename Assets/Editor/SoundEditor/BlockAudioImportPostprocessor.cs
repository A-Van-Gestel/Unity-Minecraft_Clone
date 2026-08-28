using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// Applies the project's block-audio import settings to every clip imported under
    /// <see cref="AUDIO_ROOT"/>, so a newly dropped-in sound pack is correct without a manual pass.
    /// </summary>
    /// <remarks>
    /// The settings are not cosmetic. Block one-shots play from 3D <c>AudioSource</c>s, and a stereo clip
    /// does not spatialize — it would sit in both ears regardless of where the voxel is. Decompress-on-load
    /// keeps the decoder off the play call for clips this short. Both were previously applied by a one-off
    /// script, which silently stopped covering anything imported afterwards.
    /// </remarks>
    public class BlockAudioImportPostprocessor : AssetPostprocessor
    {
        /// <summary>Only clips under this folder are touched; audio elsewhere keeps Unity's defaults.</summary>
        private const string AUDIO_ROOT = "Assets/Audio/";

        /// <summary>
        /// Sets mono, Vorbis and decompress-on-load before the clip is imported.
        /// </summary>
        /// <remarks>
        /// Runs in <c>OnPreprocessAudio</c> rather than reimporting afterwards: settings applied here are
        /// part of the first import, so a fresh clone or a re-import produces the same result without a
        /// second pass.
        /// </remarks>
        private void OnPreprocessAudio()
        {
            if (assetImporter is not AudioImporter importer) return;
            if (!assetPath.StartsWith(AUDIO_ROOT)) return;

            // Only stamp a clip the first time. Re-stamping on every reimport would silently revert a
            // deliberate per-clip override made in the inspector.
            if (!string.IsNullOrEmpty(importer.userData)) return;

            importer.forceToMono = true;
            importer.loadInBackground = false;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            importer.defaultSampleSettings = settings;

            importer.userData = STAMP;
        }

        /// <summary>Marks a clip as already stamped, so later reimports leave manual overrides alone.</summary>
        private const string STAMP = "blockAudioDefaults";
    }
}
