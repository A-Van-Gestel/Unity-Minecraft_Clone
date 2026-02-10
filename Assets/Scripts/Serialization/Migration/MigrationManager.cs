using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Serialization.Migration
{
    public static class MigrationManager
    {
        // Registry of all available migrations.
        // In the future, you can use Reflection to auto-populate this.
        private static readonly List<WorldMigration> s_migrations = new List<WorldMigration>();

        public static void Register(WorldMigration migration)
        {
            s_migrations.Add(migration);
        }

        public static bool RequiresMigration(int currentVersion, int latestVersion)
        {
            return currentVersion < latestVersion;
        }

        public static List<WorldMigration> GetMigrationPath(int startVersion, int targetVersion)
        {
            var path = new List<WorldMigration>();
            int current = startVersion;

            while (current < targetVersion)
            {
                var step = s_migrations.FirstOrDefault(m => m.SourceVersion == current);
                if (step == null)
                {
                    Debug.LogError($"MigrationManager: No migration script found for version {current} -> {current + 1}. Save data may be incompatible.");
                    return null; // Migration impossible
                }

                path.Add(step);
                current = step.TargetVersion;
            }

            return path;
        }
    }
}
