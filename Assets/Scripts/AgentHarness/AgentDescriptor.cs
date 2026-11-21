// The package's runtime types exist only in the Editor and in a development player built with its dev toggle on.
#if UNITY_EDITOR || (ENABLE_RUNTIME_PIPELINE_DEV && (UNITY_INCLUDE_INSTRUMENTATION || ENABLE_PROFILER))
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Unity.Pipeline;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AgentHarness
{
    /// <summary>
    /// Publishes the runtime server's descriptor (<c>.unity-pipeline-runtime-port</c>) when the package cannot. The
    /// package restricts the file to the current user through <c>whoami</c>/<c>icacls</c>, which cannot be started in
    /// an IL2CPP player, so it never writes the file and no client can connect. This takes over the package's own
    /// descriptor (same identity, endpoints, token and heartbeat) and writes it without that restriction.
    /// </summary>
    public static class AgentDescriptor
    {
        private const string PUBLISHER_TYPE = "Unity.Pipeline.PipelineDescriptorPublisher";
        private const string LIVE_FILE_TYPE = "Unity.Pipeline.LiveDescriptorFile";
        private const string REGISTRY_TYPE = "Unity.Pipeline.IEndpointRegistrationService";
        private const string PUBLISHED_FILE_FIELD = "s_File";
        private const string PATH_FIELD = "m_FilePath";
        private const string IDENTITY_FIELD = "m_Identity";
        private const string REGISTRY_FIELD = "m_Registry";
        private const string TOKEN_FIELD = "m_GetEvalToken";
        private const string BUILD_METHOD = "BuildDescriptor";
        private const string ENDPOINTS_METHOD = "GetEndpoints";
        private const string DISPOSE_METHOD = "Dispose";

        private const BindingFlags STATIC_MEMBER = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags INSTANCE_MEMBER = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // LiveDescriptorFile.DefaultHeartbeatInterval: the descriptor's lastHeartbeat advances as the package's would.
        private const int HEARTBEAT_MILLISECONDS = 15000;

        // Bounds the quit-time wait for a heartbeat write already in flight.
        private const int HEARTBEAT_STOP_MILLISECONDS = 1000;

        [NoAutoStaticsCleanup] // immutable pattern
        private static readonly Regex s_pidField = new Regex(@"""pid""\s*:\s*(\d+)", RegexOptions.CultureInvariant);

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static Timer s_heartbeat;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static string s_publishedPath;

        // The package members this class reflects into, resolved against the loaded Unity.Pipeline assembly.
        private sealed class Members
        {
            public FieldInfo PublishedFile, FilePath, Identity, Registry, Token;
            public MethodInfo Build, Endpoints, Dispose;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Application.quitting -= OnQuitting;
            s_heartbeat?.Dispose();
            s_heartbeat = null;
            s_publishedPath = null;
        }

        /// <summary>Checks that every package member this class reflects into still exists.</summary>
        /// <param name="error">What is missing, when it returns false.</param>
        /// <returns>True when every member resolves.</returns>
        public static bool CheckPackageMembers(out string error) => TryResolve(out _, out error);

        private static bool TryResolve(out Members members, out string error)
        {
            members = null;
            Assembly pipeline = typeof(RuntimePipelineBootstrap).Assembly;
            Type publisher = pipeline.GetType(PUBLISHER_TYPE);
            Type liveFile = pipeline.GetType(LIVE_FILE_TYPE);
            Type registry = pipeline.GetType(REGISTRY_TYPE);
            if (publisher == null || liveFile == null || registry == null)
            {
                error = $"Unity.Pipeline no longer has {PUBLISHER_TYPE}, {LIVE_FILE_TYPE} or {REGISTRY_TYPE}.";
                return false;
            }

            var resolved = new Members
            {
                PublishedFile = publisher.GetField(PUBLISHED_FILE_FIELD, STATIC_MEMBER),
                FilePath = liveFile.GetField(PATH_FIELD, INSTANCE_MEMBER),
                Identity = liveFile.GetField(IDENTITY_FIELD, INSTANCE_MEMBER),
                Registry = liveFile.GetField(REGISTRY_FIELD, INSTANCE_MEMBER),
                Token = liveFile.GetField(TOKEN_FIELD, INSTANCE_MEMBER),
                Build = liveFile.GetMethod(BUILD_METHOD, STATIC_MEMBER),
                Endpoints = registry.GetMethod(ENDPOINTS_METHOD, INSTANCE_MEMBER),
                Dispose = liveFile.GetMethod(DISPOSE_METHOD, INSTANCE_MEMBER, null, Type.EmptyTypes, null),
            };

            if (resolved.PublishedFile == null || resolved.FilePath == null || resolved.Identity == null
                || resolved.Registry == null || resolved.Token == null || resolved.Build == null
                || resolved.Endpoints == null || resolved.Dispose == null)
            {
                error = $"Unity.Pipeline's {LIVE_FILE_TYPE} members changed; the descriptor fallback needs updating.";
                return false;
            }

            members = resolved;
            error = null;
            return true;
        }

        /// <summary>
        /// Leaves a descriptor this process wrote alone; otherwise (none, or one left by a killed or older player) stops
        /// the package's failing writer and publishes the same descriptor itself, refreshed on the package's heartbeat
        /// and deleted when the player quits.
        /// </summary>
        /// <param name="error">Why no descriptor could be published, when it returns false.</param>
        /// <returns>True when a descriptor is on disk.</returns>
        public static bool TryEnsurePublished(out string error)
        {
            if (!TryResolve(out Members members, out error))
                return false;

            object packageFile = members.PublishedFile.GetValue(null);
            if (packageFile == null)
            {
                error = "Unity.Pipeline is not publishing a descriptor.";
                return false;
            }

            var path = (string)members.FilePath.GetValue(packageFile);
            bool ownFile;
            try
            {
                ownFile = File.Exists(path) && IsOwnDescriptor(path);
            }
            catch (IOException)
            {
                ownFile = false; // unreadable: the takeover's write reports whether the path is usable
            }

            if (ownFile)
                return true;

            object identity = members.Identity.GetValue(packageFile);
            object registry = members.Registry.GetValue(packageFile);
            var getToken = (Func<string>)members.Token.GetValue(packageFile);

            // Its heartbeat would keep failing (and logging) every interval; nothing of its own is on disk to delete.
            members.Dispose.Invoke(packageFile, null);
            members.PublishedFile.SetValue(null, null);

            try
            {
                Write(members, path, identity, registry, getToken);
            }
            catch (Exception ex)
            {
                error = $"Could not write the runtime descriptor '{path}': {ex.Message}";
                return false;
            }

            s_heartbeat?.Dispose();
            s_publishedPath = path;
            s_heartbeat = new Timer(_ => Refresh(members, path, identity, registry, getToken), null,
                HEARTBEAT_MILLISECONDS, HEARTBEAT_MILLISECONDS);
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
            Debug.LogWarning($"[Agent] Wrote the runtime descriptor '{path}' without the package's current-user "
                             + "restriction (its whoami/icacls step cannot run in this player).");
            return true;
        }

        private static void Refresh(Members members, string path, object identity, object registry, Func<string> getToken)
        {
            try
            {
                // Another player started from the same folder now owns the file; overwriting it would hide that one.
                if (File.Exists(path) && !IsOwnDescriptor(path))
                {
                    s_heartbeat?.Dispose();
                    return;
                }

                Write(members, path, identity, registry, getToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Agent] Failed to refresh the runtime descriptor '{path}': {ex.Message}");
            }
        }

        private static void Write(Members members, string path, object identity, object registry, Func<string> getToken)
        {
            object endpoints = members.Endpoints.Invoke(registry, null);
            object descriptor = members.Build.Invoke(null, new[] { identity, endpoints, getToken(), DateTime.UtcNow });

            // Written aside and moved into place, so a client never reads a half-written file.
            string tempPath = path + ".agent.tmp";
            File.WriteAllText(tempPath, descriptor.ToString()); // a JObject prints indented JSON
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }

        private static void OnQuitting()
        {
            Application.quitting -= OnQuitting;
            StopHeartbeat();
            string path = s_publishedPath;
            s_publishedPath = null;
            try
            {
                if (path != null && File.Exists(path) && IsOwnDescriptor(path))
                    File.Delete(path);
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"[Agent] Could not delete the runtime descriptor '{path}': {ex.Message}");
            }
        }

        // Waits for a heartbeat write already in flight, so none can recreate the file after the delete.
        private static void StopHeartbeat()
        {
            Timer heartbeat = s_heartbeat;
            s_heartbeat = null;
            if (heartbeat == null)
                return;

            // Disposed only once signaled: after a timeout the timer still signals it when the late write ends.
            var stopped = new ManualResetEvent(false);
            if (!heartbeat.Dispose(stopped) || stopped.WaitOne(HEARTBEAT_STOP_MILLISECONDS))
                stopped.Dispose();
        }

        // True when the descriptor names this process, as the package's own delete checks.
        private static bool IsOwnDescriptor(string path)
        {
            Match pid = s_pidField.Match(File.ReadAllText(path));
            return pid.Success && int.TryParse(pid.Groups[1].Value, out int filePid)
                               && filePid == Process.GetCurrentProcess().Id;
        }
    }
}
#endif
