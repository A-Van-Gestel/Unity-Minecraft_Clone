using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Generate <c>X</c> & <c>Z</c> coordinates in a spiral starting from 0, 0.
    /// Source: <see href="https://discussions.unity.com/t/how-to-generate-a-grid-from-the-center/171186/2">UNITY Discussions - How to generate a grid from the center?</see>
    /// </summary>
    public class SpiralLoop
    {
        #region Properties

        public int X { get; private set; } = 0;
        public int Z { get; private set; } = 0;

        #endregion


        #region Public Methods

        /// <summary>
        /// Generates the next <c>X</c> & <c>Z</c> coordinates, access the public <c>X</c> & <c>Z</c> properties to get them.
        /// </summary>
        public void Next()
        {
            // Initial case: when both X and Z are 0, move to the first point (1, 0).
            if (X == 0 && Z == 0)
            {
                X = 1;
                return;
            }

            if (Mathf.Abs(X - 0.5f) > Mathf.Abs(Z) && Mathf.Abs(X) > (-Z + 0.5f))
                Z += (int)Mathf.Sign(X);
            else
                X -= (int)Mathf.Sign(Z);
        }

        /// <summary>
        /// Generates the next <c>X</c> & <c>Z</c> coordinates and returns them as a new <c>Vector2</c>.
        /// </summary>
        public Vector2 NextPoint()
        {
            Next();
            return new Vector2(X, Z);
        }

        /// <summary>
        /// Resets the <c>X</c> & <c>Z</c> coordinates to 0, 0.
        /// </summary>
        public void Reset()
        {
            X = 0;
            Z = 0;
        }

        #endregion
    }
}