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
    /// resident if decompressed. Fluid emitter loops (S3) are the third case and share neither: they play
    /// from 3D sources, so they must be mono like the one-shots, but they are long loops, so they are kept
    /// compressed in memory rather than decompressed. Streaming is wrong for them too — several emitters
    /// can be audible at once, and each stream is a decoder the mix does not need. Music is the fourth case
    /// and shares the ambience profile: 2D, stereo, streamed — only one track is ever audible, and these are
    /// the longest clips in the project. The block and ambience profiles were previously applied by a one-off
    /// script, which silently stopped covering anything imported afterwards.
    /// </remarks>
    public class BlockAudioImportPostprocessor : AssetPostprocessor
    {
        /// <summary>Only clips under this folder are touched; audio elsewhere keeps Unity's defaults.</summary>
        private const string AUDIO_ROOT = "Assets/Audio/";

        /// <summary>Clips under this folder get the 2D looping-bed profile instead of the one-shot profile.</summary>
        private const string AMBIENCE_ROOT = "Assets/Audio/Ambience/";

        /// <summary>Clips under this folder get the 3D looping-emitter profile (S3).</summary>
        private const string EMITTER_ROOT = "Assets/Audio/Emitters/";

        /// <summary>Clips under this folder get the 2D streamed-music profile.</summary>
        private const string MUSIC_ROOT = "Assets/Audio/Music/";

        /// <summary>Marks a one-shot clip as stamped, so later reimports leave manual overrides alone.</summary>
        private const string BLOCK_STAMP = "blockAudioDefaults";

        /// <summary>Marks an ambience bed as stamped. Distinct from <see cref="BLOCK_STAMP"/> so the two
        /// profiles cannot be mistaken for one another if a clip is ever moved between the folders.</summary>
        private const string AMBIENCE_STAMP = "ambienceAudioDefaults";

        /// <summary>Marks a fluid emitter loop as stamped. Distinct from the other two for the same reason.</summary>
        private const string EMITTER_STAMP = "emitterAudioDefaults";

        /// <summary>Marks a music track as stamped. Distinct from the other three for the same reason.</summary>
        private const string MUSIC_STAMP = "musicAudioDefaults";

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
            bool isEmitter = assetPath.StartsWith(EMITTER_ROOT);
            bool isMusic = assetPath.StartsWith(MUSIC_ROOT);
            string stamp = isAmbience ? AMBIENCE_STAMP :
                isEmitter ? EMITTER_STAMP :
                isMusic ? MUSIC_STAMP : BLOCK_STAMP;

            // Only stamp a clip the first time. Re-stamping on every reimport would silently revert a
            // deliberate per-clip override made in the inspector.
            // Matched on OUR marker, not on userData being non-empty: another tool's data in that field is
            // not evidence this clip was ever configured here, and treating it as such leaves the clip at
            // Unity's defaults — exactly the state this postprocessor exists to prevent.
            if (importer.userData != null && importer.userData.Contains(stamp)) return;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;

            if (isMusic)
            {
                // The beds' profile, for the same reasons and one more: music plays from a 2D source where
                // the stereo image is the whole point, and a multi-minute track is the largest thing in the
                // project to hold as resident PCM. Streaming keeps one decoder alive for the single track
                // that is playing, which is exactly the shape of this layer.
                importer.forceToMono = false;
                importer.loadInBackground = true;
                settings.loadType = AudioClipLoadType.Streaming;
            }
            else if (isAmbience)
            {
                // Streamed, not decompressed: a 30 s stereo loop costs megabytes of resident PCM, and a bed
                // that fades in over seconds has no need for the sample to be ready on the same frame.
                importer.forceToMono = false;
                importer.loadInBackground = true;
                settings.loadType = AudioClipLoadType.Streaming;
            }
            else if (isEmitter)
            {
                // Mono because a stereo clip does not spatialize on a 3D source, and the whole point of an
                // emitter is that the player can turn toward it. Compressed in memory rather than
                // decompressed: these are loops, not one-shots, and the decode cost is paid by a source
                // that fades in over a second anyway.
                importer.forceToMono = true;
                importer.loadInBackground = true;
                settings.loadType = AudioClipLoadType.CompressedInMemory;
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
