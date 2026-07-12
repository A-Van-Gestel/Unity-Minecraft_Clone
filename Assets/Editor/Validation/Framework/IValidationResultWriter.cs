using System.IO;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Serializes an <see cref="AggregateRunResult"/> to a machine-readable results file (CI/external tooling).
    /// The core writes to a <see cref="TextWriter"/> (so the self-test can round-trip in-memory with no disk I/O);
    /// <see cref="ValidationResultWriterExtensions.WriteToFile"/> is the thin file convenience over it.
    /// <para>Implemented today by <see cref="NUnitXmlWriter"/>; the interface exists so a JUnit (or other) writer
    /// can drop in without touching the CI entry point.</para>
    /// </summary>
    public interface IValidationResultWriter
    {
        /// <summary>The conventional file extension for this format, without the dot (e.g. "xml").</summary>
        string FileExtension { get; }

        /// <summary>Writes the full result set to <paramref name="output"/>.</summary>
        /// <param name="result">The aggregate result to serialize.</param>
        /// <param name="output">The destination writer.</param>
        void Write(AggregateRunResult result, TextWriter output);
    }

    /// <summary>File-writing convenience over <see cref="IValidationResultWriter"/>.</summary>
    public static class ValidationResultWriterExtensions
    {
        /// <summary>Writes the result to <paramref name="path"/> as UTF-8, creating the parent directory if needed.</summary>
        /// <param name="writer">The format writer.</param>
        /// <param name="result">The aggregate result to serialize.</param>
        /// <param name="path">The destination file path.</param>
        public static void WriteToFile(this IValidationResultWriter writer, AggregateRunResult result, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using StreamWriter sw = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
            writer.Write(result, sw);
        }
    }
}
