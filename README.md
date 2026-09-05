<img width="500" height="392" alt="risovach com(1)" src="https://github.com/user-attachments/assets/1d154340-f419-4fe1-af4a-5c1639a8de39" />

---

[🇷🇺 Русский](README.rus.md)

# Fast Port Scanner

High-speed TCP port scanner for IPv4 addresses and CIDR networks.

Designed for fast network discovery with a simple CLI, high concurrency and minimal overhead.

## Features

* TCP connect scanning
* IPv4 addresses and CIDR networks
* Mixed IP/CIDR input
* Up to 1,000,000 targets per scan
* Configurable connection timeout
* Configurable concurrency
* Duplicate target removal
* Ctrl+C cancellation
* Live scan progress
* Sorted output
* No external dependencies

## Usage

```text
fps.exe -i ip.txt -p 80
```

Available options:

```text
-i, --input        Input file with IPv4 addresses/CIDR
-p, --port         TCP port to scan
-o, --output       Output file (default: results.txt)
-t, --timeout      Connection timeout in ms (default: 500)
-c, --concurrency  Maximum concurrent connections (default: 2000)
-h, --help         Show this help
```

Example:

```text
fps.exe -i ip.txt -p 8291 -o results.txt -t 500 -c 2000
```

## Input

The input file can contain individual IPv4 addresses, CIDR networks, or both:

```text
192.168.1.1
192.168.1.10
192.168.1.0/24
10.0.0.0/24
```

Comments are also supported:

```text
192.168.1.1 # router
10.0.0.0/24 # network
```

## Output

The output file contains one IP address per line for hosts where the specified TCP port accepted a connection:

```text
192.168.1.1
192.168.1.10
192.168.1.25
```

## Performance

The scanner is designed for high-speed TCP scanning and uses asynchronous TCP connections with bounded concurrency.

Example test:

```text
65,536 targets
TCP port: 500
Timeout: 500 ms
Concurrency: 2,000

Time: 17.39 s
```

Actual scan time depends heavily on network conditions, latency, filtering and the number of hosts that do not respond.

## Notes

* The scanner performs a direct TCP connection attempt. It does not ping hosts first.
* Only IPv4 targets are supported.
* A low timeout can improve scan speed but may miss slower hosts.
* Higher concurrency can significantly improve performance, but the optimal value depends on the network and system.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

