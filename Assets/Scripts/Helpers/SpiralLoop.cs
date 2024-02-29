using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Generate X & Z coordinates in a spiral form from 0, 0.
    /// Source: https://discussions.unity.com/t/how-to-generate-a-grid-from-the-center/171186/2
    /// </summary>
    public class SpiralLoop
    {
        public int X = 0;
        public int Z = 0;

        /// <summary>
        /// Generates the next X and Z coordinates, access the public X & Z properties to get them.
        /// </summary>
        public void Next()
        {
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
        /// Generates the next X and Z coordinates and returns them as a new Vector2.
        /// </summary>
        public Vector2 NextPoint()
        {
            Next();
            return new Vector2(X, Z);
        }

        /// <summary>
        /// Resets the X & Z coordinates to 0, 0.
        /// </summary>
        public void Reset()
        {
            X = 0;
            Z = 0;
        }
    }
}