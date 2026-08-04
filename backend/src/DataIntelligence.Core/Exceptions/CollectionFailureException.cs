using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Exceptions;

/// <summary>
/// A collection failure that already knows how it should be categorised on the run record.
/// Thrown only where a category is genuinely known at the throw site; everything else is
/// caught at the top of the run and recorded as <see cref="CollectionFailureCategory.Unknown"/>.
/// </summary>
public sealed class CollectionFailureException : Exception
{
    public CollectionFailureException(
        CollectionFailureCategory category,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
    }

    public CollectionFailureCategory Category { get; }
}
