using System;
using System.Text;
using Data.Enums;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// A single credit entry representing a third-party asset, library, or resource
    /// used in the project. Serialized as part of <see cref="CreditsDatabase"/>.
    /// </summary>
    [Serializable]
    public class CreditEntry
    {
        [Header("Core")]
        [Tooltip("Display name of the asset or library.")]
        public string name;

        [Tooltip("Author or organization name.")]
        public string author;

        [Tooltip("Category this entry belongs to.")]
        public CreditCategory category;

        [Header("License")]
        [Tooltip("The license type. Select 'Custom' to use the custom license text field.")]
        public LicenseType licenseType;

        [Tooltip("Only used when License Type is set to 'Custom'.")]
        public string customLicenseText;

        [Header("Details")]
        [Tooltip("URL to the source repository or website.")]
        public string url;

        [Tooltip("Brief description of how this asset is used in the project.")]
        [TextArea(1, 3)]
        public string usageDescription;

        [Tooltip("Original source files used from this asset (optional).")]
        public string[] sourceFiles;

        [Tooltip("Paths in this project where the asset is located (optional).")]
        public string[] projectFiles;

        [Tooltip("Version of the asset (optional).")]
        public string version;

        [Tooltip("Additional notes (optional).")]
        [TextArea(1, 3)]
        public string notes;

        /// <summary>
        /// Returns the display string for the license type.
        /// Falls back to <see cref="customLicenseText"/> when <see cref="licenseType"/> is <see cref="LicenseType.Custom"/>.
        /// </summary>
        public string GetLicenseDisplayName()
        {
            return licenseType switch
            {
                LicenseType.MIT => "MIT",
                LicenseType.Apache2 => "Apache 2.0",
                LicenseType.GPL2 => "GPL 2.0",
                LicenseType.GPL3 => "GPL 3.0",
                LicenseType.LGPL3 => "LGPL 3.0",
                LicenseType.CC0 => "CC0 (Public Domain)",
                LicenseType.CCBYSA3 => "CC-BY-SA 3.0",
                LicenseType.SIL_OFL => "SIL Open Font License 1.1",
                LicenseType.Proprietary => "Proprietary",
                LicenseType.NotApplicable => "Not Applicable",
                LicenseType.Custom => !string.IsNullOrEmpty(customLicenseText) ? customLicenseText : "Custom",
                _ => licenseType.ToString(),
            };
        }

        /// <summary>
        /// Formats this entry as a rich-text block for display.
        /// When <paramref name="includeTmpLinks"/> is true, the name is wrapped in TMP
        /// <c>&lt;link&gt;</c> tags (for runtime UI). When false, plain bold text is used (for editor preview).
        /// </summary>
        public string FormatRichText(bool includeTmpLinks = true)
        {
            StringBuilder sb = new StringBuilder();

            bool hasUrl = !string.IsNullOrEmpty(url);

            if (hasUrl && includeTmpLinks)
                sb.Append($"<link=\"{url}\"><u><b>{name}</b></u></link>");
            else if (hasUrl)
                sb.Append($"<b>{name}</b> (<i>{url}</i>)");
            else
                sb.Append($"<b>{name}</b>");

            if (!string.IsNullOrEmpty(author))
                sb.Append($" by {author}");
            sb.AppendLine();

            sb.AppendLine($"  License: {GetLicenseDisplayName()}");

            if (!string.IsNullOrEmpty(usageDescription))
                sb.AppendLine($"  Usage: {usageDescription}");

            if (!string.IsNullOrEmpty(version))
                sb.AppendLine($"  Version: {version}");

            if (sourceFiles != null && sourceFiles.Length > 0)
                sb.AppendLine($"  Source Files: {string.Join(", ", sourceFiles)}");

            if (projectFiles != null && projectFiles.Length > 0)
                sb.AppendLine($"  Project Files: {string.Join(", ", projectFiles)}");

            if (!string.IsNullOrEmpty(notes))
                sb.AppendLine($"  Notes: {notes}");

            return sb.ToString().TrimEnd();
        }
    }
}
