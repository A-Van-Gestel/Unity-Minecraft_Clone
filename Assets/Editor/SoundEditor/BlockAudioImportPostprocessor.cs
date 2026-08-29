using UnityEditor;
using UnityEngine;

namespace Editor.SoundEditor
{
    /// <summary>
    /// Applies the project's audio import settings to every clip imported under <see cref="AUDIO_ROOT"/>, so a
    /// newly dropped-in sound pack is correct without a manual pass.
    /// </summary>
    /// <remarks>
    /// The settings are not cosmetic, and the two profiles are opposites because the sources are. Block
    /// one-shots play from 3D <c>AudioSource</c>s, where a stereo clip does not spatialize — it would sit in
    /// both ears regardless of where the voxel is — and decompress-on-load keeps the decoder off the play call
    /// for clips that short. Ambience beds are the reverse case: they play from 2D sources where the stereo
    /// image is the entire point, and they are half-minute loops that would each hold megabytes of PCM
    /// resident if decompressed. Both were previously applied by a one-off script, which silently stopped
    /// covering anything imported afterwards.
    /// </remarks>
    public class BlockAudioImportPostprocessor : AssetPostprocessor
    {
        /// <summary>Only clips under this folder are touched; audio elsewhere keeps Unity's defaults.</summary>
        private const string AUDIO_ROOT = "Assets/Audio/";

        /// <summary>Clips under this folder get the 2D looping-bed profile instead of the one-shot profile.</summary>
        private const string AMBIENCE_ROOT = "Assets/Audio/Ambience/";

        /// <summary>Marks a one-shot clip as stamped, so later reimports leave manual overrides alone.</summary>
        private const string BLOCK_STAMP = "blockAudioDefaults";

        /// <summary>Marks an ambience bed as stamped. Distinct from <see cref="BLOCK_STAMP"/> so the two
        /// profiles cannot be mistaken for one another if a clip is ever moved between the folders.</summary>
        private const string AMBIENCE_STAMP = "ambienceAudioDefaults";

        /// <summary>
        /// Applies the profile the clip's folder calls for, before the clip is imported.
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

            bool isAmbience = assetPath.StartsWith(AMBIENCE_ROOT);
            string stamp = isAmbience ? AMBIENCE_STAMP : BLOCK_STAMP;

            // Only stamp a clip the first time. Re-stamping on every reimport would silently revert a
            // deliberate per-clip override made in the inspector.
            // Matched on OUR marker, not on userData being non-empty: another tool's data in that field is
            // not evidence this clip was ever configured here, and treating it as such leaves the clip at
            // Unity's defaults — exactly the state this postprocessor exists to prevent.
            if (importer.userData != null && importer.userData.Contains(stamp)) return;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;

            if (isAmbience)
            {
                // Streamed, not decompressed: a 30 s stereo loop costs megabytes of resident PCM, and a bed
                // that fades in over seconds has no need for the sample to be ready on the same frame.
                importer.forceToMono = false;
                importer.loadInBackground = true;
                settings.loadType = AudioClipLoadType.Streaming;
            }
            else
            {
                importer.forceToMono = true;
                importer.loadInBackground = false;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
            }

            importer.defaultSampleSettings = settings;

            // Appended rather than assigned: userData is shared project-wide, so overwriting it would
            // destroy whatever another importer or tool put there.
            importer.userData = string.IsNullOrEmpty(importer.userData)
                ? stamp
                : importer.userData + ";" + stamp;
        }
    }
}
