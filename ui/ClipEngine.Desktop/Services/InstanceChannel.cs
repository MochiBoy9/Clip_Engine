using System.IO.Pipes;
using System.Text;

namespace ClipEngine.Desktop.Services;

// Lets a second launch hand its file to the copy that's already running, so opening a clip
// with Clip Engine while it sits in the tray imports it there instead of doing nothing.
internal static class InstanceChannel
{
    private static readonly string PipeName = $"ClipEngine.Desktop.{Environment.UserName}";

    // An empty message just brings the window forward.
    public static bool TrySend(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Calls onMessage on a background thread for every message, until the token is cancelled.
    public static void Listen(Action<string> onMessage, CancellationToken cancel)
    {
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                                       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancel);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    onMessage(await reader.ReadToEndAsync(cancel));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }
}
