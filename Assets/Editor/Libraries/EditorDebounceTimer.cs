using System;
using UnityEditor;

namespace Editor.Libraries
{
    /// <summary>
    /// A lightweight debounce timer for editor GUI controls. Defers an action until a
    /// configurable delay has elapsed since the last <see cref="Request"/> call, preventing
    /// expensive operations (e.g. terrain regeneration) from firing on every intermediate
    /// slider value while dragging.
    /// </summary>
    public class EditorDebounceTimer
    {
        private readonly double _delaySeconds;
        private double _scheduledTime;
        private Action _pendingAction;

        /// <summary>
        /// Creates a new debounce timer.
        /// </summary>
        /// <param name="delaySeconds">How long to wait (in seconds) after the last request before firing.</param>
        public EditorDebounceTimer(double delaySeconds)
        {
            _delaySeconds = delaySeconds;
        }

        /// <summary>
        /// Returns true if there is a pending action that has not yet fired.
        /// </summary>
        public bool IsPending => _pendingAction != null;

        /// <summary>
        /// Schedules (or reschedules) the given action. Each call resets the timer.
        /// </summary>
        /// <param name="action">The action to invoke after the delay.</param>
        public void Request(Action action)
        {
            _pendingAction = action;
            _scheduledTime = EditorApplication.timeSinceStartup + _delaySeconds;
        }

        /// <summary>
        /// Cancels any pending action without firing it.
        /// </summary>
        public void Cancel()
        {
            _pendingAction = null;
        }

        /// <summary>
        /// Call this every frame (e.g. from <c>OnGUI</c> or an <c>EditorApplication.update</c> callback).
        /// Fires the pending action if the delay has elapsed, then clears the timer.
        /// </summary>
        public void Poll()
        {
            if (_pendingAction == null) return;
            if (EditorApplication.timeSinceStartup < _scheduledTime) return;

            Action action = _pendingAction;
            _pendingAction = null;
            action.Invoke();
        }
    }
}
