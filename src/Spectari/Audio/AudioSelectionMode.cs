namespace Spectari.Audio;

public enum AudioSelectionMode { None, Process, FollowWindow, Desktop, Input }

internal interface IWindowAudioTarget
{
    uint AudioProcessId { get; }
    event Action<uint>? ProcessAttached;
}
