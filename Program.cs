using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

const int DefaultTimeout = 500;
const int DefaultConcurrency = 2000;
const int MaxTargets = 1_000_000;

try
{
    Options options = ParseArguments(args);

    if (options.ShowHelp)
    {
        PrintHelp();
        return;
    }

    if (!File.Exists(options.InputFile))
        throw new FileNotFoundException($"Input file '{options.InputFile}' was not found.");

    // Pre-increase the minimum number of ThreadPool worker threads.
    // This reduces latency when starting a large number of operations abruptly.
    ThreadPool.SetMinThreads(options.Concurrency, options.Concurrency);

    Console.WriteLine();
    Console.WriteLine("Fast Port Scanner");
    Console.WriteLine();
    Console.WriteLine($"Input:       {options.InputFile}");
    Console.WriteLine($"Port:        {options.Port}");
    Console.WriteLine($"Timeout:     {options.Timeout} ms");
    Console.WriteLine($"Concurrency: {options.Concurrency}");
    Console.WriteLine();

    Console.Write("Loading targets...");
    List<uint> targets = LoadTargets(options.InputFile);

    if (targets.Count == 0)
        throw new InvalidOperationException("No valid IPv4 targets found in input file.");

    Console.WriteLine($" {targets.Count:N0}");
    Console.WriteLine();
    Console.WriteLine("Scanning...");

    using CancellationTokenSource cancellation = new();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;

        if (!cancellation.IsCancellationRequested)
            cancellation.Cancel();
    };

    Stopwatch stopwatch = Stopwatch.StartNew();

    ConcurrentBag<uint> openTargets = new();
    int completed = 0;

    ParallelOptions parallelOptions = new()
    {
        MaxDegreeOfParallelism = options.Concurrency,
        CancellationToken = cancellation.Token
    };

    Task scanTask = Parallel.ForEachAsync(
        targets,
        parallelOptions,
        async (target, ct) =>
        {
            if (await IsPortOpenAsync(
                target,
                options.Port,
                options.Timeout,
                ct))
            {
                openTargets.Add(target);
            }

            Interlocked.Increment(ref completed);
        });

    Task progressTask = ShowProgressAsync(
        targets.Count,
        openTargets,
        cancellation.Token,
        stopwatch,
        () => Volatile.Read(ref completed));

    try
    {
        await scanTask;
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C - normal scan termination.
    }

    stopwatch.Stop();

    await progressTask;

    if (cancellation.IsCancellationRequested)
    {
        Console.WriteLine("\n\nScan cancelled.");
        Console.WriteLine($"Completed: {completed:N0} / {targets.Count:N0}");
        Console.WriteLine($"Open:      {openTargets.Count:N0}");
        Console.WriteLine($"Time:      {stopwatch.Elapsed.TotalSeconds:F2} s");
    }
    else
    {
        Console.WriteLine("\n\nScan complete.");
        Console.WriteLine($"Targets:  {targets.Count:N0}");
        Console.WriteLine($"Open:     {openTargets.Count:N0}");
        Console.WriteLine($"Time:     {stopwatch.Elapsed.TotalSeconds:F2} s");

        List<uint> resultList = openTargets.ToList();

        await SaveResultsAsync(
            options.OutputFile,
            resultList);

        Console.WriteLine($"\nResults saved to {options.OutputFile}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n\nError: {ex.Message}");
}
finally
{
    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey(true);
}


// ============================================================
// TCP scanner
// ============================================================

async Task<bool> IsPortOpenAsync(
    uint target,
    int port,
    int timeout,
    CancellationToken ct)
{
    using Socket socket = new(
        AddressFamily.InterNetwork,
        SocketType.Stream,
        ProtocolType.Tcp);

    socket.LingerState = new LingerOption(true, 0);

    try
    {
        IPAddress address = UInt32ToIPAddressFast(target);

        ValueTask connectTask = socket.ConnectAsync(
            address,
            port,
            ct);

        // Successful connection completed synchronously.
        if (connectTask.IsCompletedSuccessfully)
            return true;

        // WaitAsync limits the connection wait time.
        // On TimeoutException the socket will be disposed via using.
        await connectTask.AsTask().WaitAsync(
            TimeSpan.FromMilliseconds(timeout),
            ct);

        return true;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Cancel the entire scan on Ctrl+C.
        throw;
    }
    catch (TimeoutException)
    {
        // TCP connection did not establish within timeout.
        return false;
    }
    catch (SocketException)
    {
        // Connection refused, unreachable, reset, etc.
        return false;
    }
    catch (ObjectDisposedException)
    {
        // Socket was closed during cancellation/timeout.
        return false;
    }
}


// ============================================================
// Progress
// ============================================================

async Task ShowProgressAsync(
    int total,
    ConcurrentBag<uint> openTargets,
    CancellationToken cancellationToken,
    Stopwatch stopwatch,
    Func<int> getCompleted)
{
    const int barWidth = 30;

    string[] animation =
    {
        "|",
        "/",
        "-",
        "\\"
    };

    int animationIndex = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        int completed = getCompleted();
        int openCount = openTargets.Count;

        double percent =
            total == 0
                ? 100.0
                : completed * 100.0 / total;

        if (percent > 100.0)
            percent = 100.0;

        int filled =
            (int)(barWidth * percent / 100.0);

        string bar =
            new string('=', Math.Min(filled, barWidth)) +
            new string(
                ' ',
                Math.Max(0, barWidth - filled));

        Console.Write(
            $"\rScanning {animation[animationIndex]} " +
            $"[{bar}] " +
            $"{percent,6:F1}% " +
            $"{completed:N0}/{total:N0} " +
            $"Open: {openCount:N0} " +
            $"{stopwatch.Elapsed.TotalSeconds:F1}s");

        animationIndex =
            (animationIndex + 1) % animation.Length;

        if (completed >= total)
            break;

        await Task.Delay(
            300,
            CancellationToken.None);
    }
}


// ============================================================
// Target loading
// ============================================================

List<uint> LoadTargets(string file)
{
    HashSet<uint> targets = new();

    foreach (string rawLine in File.ReadLines(file))
    {
        string line = rawLine.Trim();

        if (string.IsNullOrWhiteSpace(line))
            continue;

        // Support for comments:
        // 192.168.1.1 # router
        int commentIndex = line.IndexOf('#');

        if (commentIndex >= 0)
            line = line[..commentIndex].Trim();

        if (string.IsNullOrWhiteSpace(line))
            continue;

        if (line.Contains('/'))
        {
            AddCidr(line, targets);
        }
        else
        {
            if (!IPAddress.TryParse(
                    line,
                    out IPAddress? address) ||
                address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new FormatException(
                    $"Invalid IPv4 address or CIDR: '{line}'");
            }

            targets.Add(
                IPAddressToUInt32(address));

            CheckTargetLimit(targets.Count);
        }
    }

    return targets.ToList();
}


// ============================================================
// CIDR expansion
// ============================================================

void AddCidr(
    string value,
    HashSet<uint> targets)
{
    string[] parts = value.Split('/');

    if (parts.Length != 2)
    {
        throw new FormatException(
            $"Invalid CIDR: '{value}'");
    }

    if (!IPAddress.TryParse(
            parts[0],
            out IPAddress? address) ||
        address.AddressFamily != AddressFamily.InterNetwork)
    {
        throw new FormatException(
            $"Invalid IPv4 address in CIDR: '{value}'");
    }

    if (!int.TryParse(
            parts[1],
            out int prefixLength) ||
        prefixLength < 0 ||
        prefixLength > 32)
    {
        throw new FormatException(
            $"Invalid CIDR prefix: '{value}'");
    }

    uint ip = IPAddressToUInt32(address);

    uint mask =
        prefixLength == 0
            ? 0
            : uint.MaxValue << (32 - prefixLength);

    uint network = ip & mask;

    ulong count =
        1UL << (32 - prefixLength);

    if (count > MaxTargets ||
        (ulong)targets.Count + count > MaxTargets)
    {
        throw new InvalidOperationException(
            $"Target list exceeds maximum limit of {MaxTargets:N0} addresses.");
    }

    for (ulong i = 0; i < count; i++)
    {
        targets.Add(
            network + (uint)i);
    }
}


// ============================================================
// Validation
// ============================================================

void CheckTargetLimit(int count)
{
    if (count > MaxTargets)
    {
        throw new InvalidOperationException(
            $"Target list exceeds maximum limit of {MaxTargets:N0} addresses.");
    }
}


// ============================================================
// Result saving
// ============================================================

async Task SaveResultsAsync(
    string file,
    List<uint> results)
{
    results.Sort();

    await using FileStream stream = new(
        file,
        FileMode.Create,
        FileAccess.Write,
        FileShare.Read);

    await using StreamWriter writer = new(
        stream,
        new UTF8Encoding(false));

    foreach (uint target in results)
    {
        await writer.WriteLineAsync(
            UInt32ToIPAddressFast(target).ToString());
    }
}


// ============================================================
// IP conversion
// ============================================================

uint IPAddressToUInt32(IPAddress address)
{
    byte[] bytes =
        address.GetAddressBytes();

    return
        ((uint)bytes[0] << 24) |
        ((uint)bytes[1] << 16) |
        ((uint)bytes[2] << 8) |
        bytes[3];
}

IPAddress UInt32ToIPAddressFast(uint value)
{
    return new IPAddress(
        BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReverseEndianness(value)
            : value);
}


// ============================================================
// Command line
// ============================================================

Options ParseArguments(string[] args)
{
    if (args.Length == 0)
{
    Environment.ExitCode = 1;

    return new Options
    {
        ShowHelp = true
    };
}

    string? input = null;
    string? output = null;

    int? port = null;

    int timeout = DefaultTimeout;
    int concurrency = DefaultConcurrency;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--help":
            case "-h":
                return new Options
                {
                    ShowHelp = true
                };

            case "--input":
            case "-i":
                input = GetValue(
                    args,
                    ref i,
                    "--input");
                break;

            case "--output":
            case "-o":
                output = GetValue(
                    args,
                    ref i,
                    "--output");
                break;

            case "--port":
            case "-p":
                port = ParseInt(
                    GetValue(
                        args,
                        ref i,
                        "--port"),
                    "--port");

                break;

            case "--timeout":
            case "-t":
                timeout = ParseInt(
                    GetValue(
                        args,
                        ref i,
                        "--timeout"),
                    "--timeout");

                break;

            case "--concurrency":
            case "-c":
                concurrency = ParseInt(
                    GetValue(
                        args,
                        ref i,
                        "--concurrency"),
                    "--concurrency");

                break;

            default:
                throw new ArgumentException(
                    $"Unknown argument: '{args[i]}'");
        }
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        throw new ArgumentException(
            "Missing required argument: --input");
    }

    if (!port.HasValue)
    {
        throw new ArgumentException(
            "Missing required argument: --port");
    }

    if (port.Value < 1 || port.Value > 65535)
    {
        throw new ArgumentOutOfRangeException(
            "--port",
            port.Value,
            "Port must be between 1 and 65535.");
    }

    if (timeout < 1)
    {
        throw new ArgumentOutOfRangeException(
            "--timeout",
            timeout,
            "Timeout must be greater than 0 ms.");
    }

    if (concurrency < 1)
    {
        throw new ArgumentOutOfRangeException(
            "--concurrency",
            concurrency,
            "Concurrency must be greater than 0.");
    }

    return new Options
    {
        InputFile = input,
        OutputFile = output ?? "result.txt",
        Port = port.Value,
        Timeout = timeout,
        Concurrency = concurrency
    };
}


string GetValue(
    string[] args,
    ref int index,
    string option)
{
    if (++index >= args.Length)
    {
        throw new ArgumentException(
            $"Missing value for {option}.");
    }

    string value = args[index];

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException(
            $"Missing value for {option}.");
    }

    return value;
}


int ParseInt(
    string value,
    string option)
{
    if (!int.TryParse(
            value,
            out int result))
    {
        throw new ArgumentException(
            $"Invalid integer value for {option}: '{value}'.");
    }

    return result;
}


// ============================================================
// Help
// ============================================================

void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine("Fast Port Scanner");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine(
        "  fps.exe --input ip.txt --port 80");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine(
        "  -i, --input        Input file with IPv4 addresses/CIDR");
    Console.WriteLine(
        "  -p, --port         TCP port to scan");
    Console.WriteLine(
        "  -o, --output       Output file (default: result.txt)");
    Console.WriteLine(
        "  -t, --timeout      Connection timeout in ms (default: 500)");
    Console.WriteLine(
        "  -c, --concurrency  Maximum concurrent connections (default: 2000)");
    Console.WriteLine(
        "  -h, --help         Show this help");
    Console.WriteLine();
    Console.WriteLine(
        $"Maximum targets: {MaxTargets:N0}");
    Console.WriteLine();
}


// ============================================================
// Options
// ============================================================

sealed class Options
{
    public string InputFile { get; init; } = "";

    public string OutputFile { get; init; } = "results.txt";

    public int Port { get; init; }

    public int Timeout { get; init; } = 500;

    public int Concurrency { get; init; } = 2000;

    public bool ShowHelp { get; init; }
}