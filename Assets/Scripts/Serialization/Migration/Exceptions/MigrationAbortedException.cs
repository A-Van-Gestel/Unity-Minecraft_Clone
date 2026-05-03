using System;

namespace Serialization.Migration.Exceptions
{
    /// <summary>
    /// Thrown when a user explicitly chooses to abort an in-progress migration
    /// (e.g. after being prompted about corrupted chunks).
    /// Caught by the UI layer to trigger a rollback without showing a generic error.
    /// </summary>
    public class MigrationAbortedException : Exception
    {
        public MigrationAbortedException(string message) : base(message) { }
    }
}
