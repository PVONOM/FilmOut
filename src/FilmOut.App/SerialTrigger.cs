using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using FilmOut.Core;

namespace FilmOut.App;

/// <summary>
/// A serial connection to a camera controller. Incoming lines are parsed into commands and
/// raised on the serial port's thread; outgoing lines are queued and written on a separate
/// thread, so nothing here ever blocks the output or the interface.
/// </summary>
public sealed class SerialTrigger : IDisposable
{
    private readonly SerialPort _port;
    private readonly BlockingCollection<string> _outgoing = new();
    private readonly Thread _writer;
    private readonly StringBuilder _incoming = new();
    private readonly object _incomingLock = new();

    public SerialTrigger(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            NewLine = "\n",
            Encoding = Encoding.ASCII,
            DtrEnable = true,
            RtsEnable = true,
            WriteTimeout = 500,
        };
        _port.DataReceived += OnDataReceived;
        _port.ErrorReceived += (_, e) => Failed?.Invoke($"line error ({e.EventType})");
        _port.Open();

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FilmOut serial writer" };
        _writer.Start();
    }

    public string PortName => _port.PortName;

    /// <summary>Raised on a background thread for every complete line received.</summary>
    public event Action<SerialCommand>? CommandReceived;

    public event Action<string>? Failed;

    public void Send(string line)
    {
        try { _outgoing.TryAdd(line); }
        catch (InvalidOperationException) { /* closing */ }
        catch (ObjectDisposedException) { /* closed */ }
    }

    public void Dispose()
    {
        _outgoing.CompleteAdding();
        _writer.Join(TimeSpan.FromMilliseconds(500));
        try { _port.Close(); } catch { /* already gone */ }
        _port.Dispose();
        _outgoing.Dispose();
    }

    private void WriteLoop()
    {
        try
        {
            foreach (string line in _outgoing.GetConsumingEnumerable())
            {
                try { _port.Write(line + "\n"); }
                catch (Exception ex) { Failed?.Invoke($"couldn't send “{line}”: {ex.Message}"); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        string chunk;
        try { chunk = _port.ReadExisting(); }
        catch (Exception ex) { Failed?.Invoke(ex.Message); return; }

        var lines = new List<string>();
        lock (_incomingLock)
        {
            foreach (char ch in chunk)
            {
                if (ch is '\n' or '\r')
                {
                    if (_incoming.Length > 0)
                    {
                        lines.Add(_incoming.ToString());
                        _incoming.Clear();
                    }
                }
                else if (_incoming.Length < 256)
                {
                    _incoming.Append(ch);
                }
            }
        }
        foreach (string line in lines) CommandReceived?.Invoke(SerialProtocol.Parse(line));
    }
}
