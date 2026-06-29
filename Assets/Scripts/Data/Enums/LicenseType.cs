using System.Diagnostics.CodeAnalysis;
using UnityEngine;

namespace Data.Enums
{
    /// <summary>
    /// Known license types for credit entries. Use <see cref="Custom"/> with
    /// <see cref="CreditEntry.customLicenseText"/> for uncommon or project-specific licenses.
    /// </summary>
    public enum LicenseType
    {
        [InspectorName("MIT")]
        MIT,

        [InspectorName("Apache 2.0")]
        Apache2,

        [InspectorName("GPL 2.0")]
        GPL2,

        [InspectorName("GPL 3.0")]
        GPL3,

        [InspectorName("LGPL 3.0")]
        LGPL3,

        [InspectorName("CC0 (Public Domain)")]
        CC0,

        [InspectorName("CC-BY-SA 3.0")]
        CCBYSA3,

        [InspectorName("SIL Open Font License 1.1")]
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        SIL_OFL,

        [InspectorName("Proprietary")]
        Proprietary,

        [InspectorName("Not Applicable")]
        NotApplicable,

        [InspectorName("Custom (see field)")]
        Custom,
    }
}
