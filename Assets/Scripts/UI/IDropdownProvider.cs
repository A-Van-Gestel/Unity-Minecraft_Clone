namespace UI
{
    /// <summary>
    /// Provides dynamic dropdown options for the settings UI generator.
    /// Implementations supply labels and handle bidirectional mapping between
    /// dropdown indices and the serialized field value.
    /// </summary>
    public interface IDropdownProvider
    {
        /// <summary>
        /// Returns the display labels for all available options.
        /// Called during UI generation and on each <see cref="SettingsUIGenerator.RebindValues"/> pass.
        /// </summary>
        /// <returns>Array of human-readable option labels.</returns>
        string[] GetOptionLabels();

        /// <summary>
        /// Maps the current field value to the corresponding dropdown index.
        /// Returns -1 if the value does not match any available option (e.g., a previously
        /// saved resolution that is no longer available), in which case the generator
        /// falls back to index 0.
        /// </summary>
        /// <param name="fieldValue">The current value of the settings field.</param>
        /// <returns>The dropdown index, or -1 if no match is found.</returns>
        int GetIndexFromValue(object fieldValue);

        /// <summary>
        /// Maps a dropdown index back to the value that should be stored in the settings field.
        /// </summary>
        /// <param name="index">The selected dropdown index.</param>
        /// <returns>The value to write to the settings field via reflection.</returns>
        object GetValueFromIndex(int index);
    }
}
