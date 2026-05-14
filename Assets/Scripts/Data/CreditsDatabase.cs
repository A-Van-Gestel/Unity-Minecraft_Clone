using System.Collections.Generic;
using Data.Enums;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// ScriptableObject database containing all third-party asset credits, references,
    /// and license information. Single source of truth for the in-game credits screen.
    /// </summary>
    [CreateAssetMenu(fileName = "CreditsDatabase", menuName = "Minecraft/Credits Database")]
    public class CreditsDatabase : ScriptableObject
    {
        [Tooltip("All credit entries. Displayed in-game grouped by category.")]
        [SerializeField]
        private List<CreditEntry> _entries = new List<CreditEntry>();

        /// <summary>
        /// Returns all credit entries (read-only for runtime consumers).
        /// </summary>
        public IReadOnlyList<CreditEntry> Entries => _entries;

        /// <summary>
        /// Returns the mutable entries list. Used by editor tools for CRUD operations.
        /// </summary>
        public List<CreditEntry> EditableEntries => _entries;

        /// <summary>
        /// Returns all entries matching the given category, preserving list order.
        /// </summary>
        public List<CreditEntry> GetEntriesByCategory(CreditCategory category)
        {
            List<CreditEntry> result = new List<CreditEntry>();
            foreach (CreditEntry entry in _entries)
            {
                if (entry.category == category) result.Add(entry);
            }

            return result;
        }
    }
}
