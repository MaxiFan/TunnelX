using Xunit;

namespace AppTunnel.Tests;

/// <summary>
/// LocalizationService.Instance is process-wide. Tests that change its language
/// must not run at the same time, or an English lookup can observe Persian and
/// return the source key.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}
