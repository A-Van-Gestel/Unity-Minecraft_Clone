using System.Reflection;
using Data;

namespace Editor.BlockEditor.Helpers
{
    /// <summary>
    /// Copies a <see cref="BlockType"/> field for field, for the Block Editor's edit-on-a-copy workflow.
    /// </summary>
    /// <remarks>
    /// Reflective by design, so a field added to <see cref="BlockType"/> is carried without touching this
    /// class. An enumerated copy cannot offer that guarantee, and a field it omits is destroyed silently:
    /// the copy holds the field's initializer, and saving writes that back over the asset.
    /// <para>
    /// Private fields are included, not just public ones: inspector state is conventionally
    /// <c>[SerializeField] private</c> here, so a public-only sweep would reopen exactly the hole this
    /// class closes the first time a field is added that way.
    /// </para>
    /// </remarks>
    public static class BlockTypeCloner
    {
        private static readonly FieldInfo[] s_fields =
            typeof(BlockType).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Creates a copy of a block type carrying every one of its fields.
        /// </summary>
        /// <param name="source">The block to copy.</param>
        /// <returns>A new instance with the same field values, or <c>null</c> when the source is null.</returns>
        /// <remarks>
        /// Shallow: value types and strings copy, while textures, meshes and any other reference are shared
        /// with the source. The copy exists so edits can be reverted, not to duplicate referenced assets.
        /// </remarks>
        public static BlockType Clone(BlockType source)
        {
            if (source == null) return null;

            BlockType copy = new BlockType();
            foreach (FieldInfo field in s_fields) field.SetValue(copy, field.GetValue(source));

            return copy;
        }
    }
}
