using System;

namespace Rendering
{
    /// <summary>
    /// The Render Graph pass names of one band's blur chain, built once and reused every frame.
    /// </summary>
    /// <remarks>
    /// The chain records every frame, so composing its names there would allocate per frame. The names
    /// stay exactly what concatenation gives, which keeps bands separable in the Render Graph Viewer.
    /// </remarks>
    public sealed class UIBlurPassNames
    {
        private string[] _iterations = Array.Empty<string>();

        /// <summary>Creates the names for the chain recorded under <paramref name="label"/>.</summary>
        /// <param name="label">Pass-name prefix, such as <c>UI Band Hud</c>.</param>
        public UIBlurPassNames(string label)
        {
            Label = label;
            SetGlobal = label + " Set Global";
        }

        /// <summary>Pass-name prefix shared by every pass of the chain.</summary>
        public string Label { get; }

        /// <summary>Name of the pass that publishes the blur as a global.</summary>
        public string SetGlobal { get; }

        /// <summary>Name of one Kawase iteration's pass.</summary>
        /// <param name="index">Zero-based iteration index.</param>
        /// <returns><c>"{Label} Iter {index}"</c>, the same instance on every call.</returns>
        /// <remarks>Grows on demand, so only the first frame at a higher iteration count allocates.</remarks>
        public string Iteration(int index)
        {
            if (index >= _iterations.Length) Grow(index + 1);
            return _iterations[index];
        }

        /// <summary>Extends the name table to <paramref name="count"/> iterations, keeping existing names.</summary>
        private void Grow(int count)
        {
            string[] grown = new string[count];
            Array.Copy(_iterations, grown, _iterations.Length);
            for (int i = _iterations.Length; i < count; i++) grown[i] = Label + " Iter " + i;
            _iterations = grown;
        }
    }
}
