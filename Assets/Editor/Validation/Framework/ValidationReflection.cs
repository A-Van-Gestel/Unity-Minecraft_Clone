using System;
using System.Reflection;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Shared reflection helpers for editor-validation harnesses that must drive a type's private setters
    /// in edit mode (e.g. stubbing <c>World.Instance</c> / <c>World.ChunkPool</c> / <c>World._tickCounter</c>
    /// without running <c>Awake</c>). Centralized so the recipe lives in one place rather than being
    /// re-implemented per harness (the prior copies in <c>BehaviorTestWorld</c> and
    /// <c>SectionRendererTestFixture</c> would drift independently if an accessor's name/visibility changed).
    /// </summary>
    public static class ValidationReflection
    {
        /// <summary>Invokes the (possibly non-public) setter of a static auto-property.</summary>
        /// <param name="type">The declaring type.</param>
        /// <param name="propertyName">The static property name.</param>
        /// <param name="value">The value to assign (may be null).</param>
        public static void SetStaticProperty(Type type, string propertyName, object value)
        {
            PropertyInfo prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            MethodInfo setter = prop?.GetSetMethod(nonPublic: true);
            if (setter == null)
                throw new InvalidOperationException($"Could not locate the static setter for {type.Name}.{propertyName} via reflection.");
            setter.Invoke(null, new[] { value });
        }

        /// <summary>Invokes the (possibly non-public) setter of an instance auto-property.</summary>
        /// <param name="target">The instance whose property to set.</param>
        /// <param name="propertyName">The instance property name.</param>
        /// <param name="value">The value to assign (may be null).</param>
        public static void SetInstanceProperty(object target, string propertyName, object value)
        {
            PropertyInfo prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            MethodInfo setter = prop?.GetSetMethod(nonPublic: true);
            if (setter == null)
                throw new InvalidOperationException($"Could not locate the instance setter for {target.GetType().Name}.{propertyName} via reflection.");
            setter.Invoke(target, new[] { value });
        }

        /// <summary>Writes a private instance field directly.</summary>
        /// <param name="target">The instance whose field to write.</param>
        /// <param name="fieldName">The private field name.</param>
        /// <param name="value">The value to assign.</param>
        public static void SetInstanceField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                throw new InvalidOperationException($"Could not locate the private field {target.GetType().Name}.{fieldName} via reflection.");
            field.SetValue(target, value);
        }

        /// <summary>Reads a private instance field directly (the read mirror of <see cref="SetInstanceField"/>).</summary>
        /// <param name="target">The instance whose field to read.</param>
        /// <param name="fieldName">The private field name.</param>
        /// <returns>The field's current value.</returns>
        public static object GetInstanceField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                throw new InvalidOperationException($"Could not locate the private field {target.GetType().Name}.{fieldName} via reflection.");
            return field.GetValue(target);
        }
    }
}
