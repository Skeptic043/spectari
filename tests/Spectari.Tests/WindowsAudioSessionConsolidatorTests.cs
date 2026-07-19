using Spectari.Ui;
using Xunit;

namespace Spectari.Tests;

public sealed class WindowsAudioSessionConsolidatorTests
{
    [Fact]
    public void EmptyTargetSetEnumeratesSessionsWithoutChangingThem()
    {
        WindowsAudioSessionConsolidator.Apply(new HashSet<uint>());
    }
}
