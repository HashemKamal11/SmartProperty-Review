namespace SmartProperty.Common.Localization;

/// <summary>
/// Asks for a <c>display&lt;Name&gt;</c> sibling, in the caller's language, on a string (or string list) property whose
/// name the API would not recognise by itself as a closed-list value (<c>Status</c>, <c>PropertyType</c>, <c>Kind</c>, ...).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class LocalizedValueAttribute : Attribute;

/// <summary>Stops the API adding a <c>display&lt;Name&gt;</c> sibling to a property it would otherwise translate.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class NotLocalizedAttribute : Attribute;

/// <summary>
/// Marks a human-readable message written in English by this platform (an error message). The API adds a
/// <c>display&lt;Name&gt;</c> sibling holding it in the caller's language.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class LocalizedMessageAttribute : Attribute;
