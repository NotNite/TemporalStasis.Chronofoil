using System.CommandLine;
using System.CommandLine.Binding;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Chronofoil.CaptureFile;
using Chronofoil.CaptureFile.Generated;
using TemporalStasis;
using TemporalStasis.Compression;
using TemporalStasis.Intercept;
using TemporalStasis.Proxy;
using TemporalStasis.Structs;

var captureId = Guid.NewGuid();

var rootCommand = new RootCommand("TemporalStasis.Chronofoil");
var origHostOption = new Option<string>("--host", () => "neolobby02.ffxiv.com",
    "Lobby server to forward (usually neolobbyXX.ffxiv.com for official servers)");
var origPortOption = new Option<uint>("--port", () => 54994,
    "Port of the lobby server to forward (usually 54994 for official servers)");
var lobbyProxyHostOption = new Option<string>("--lobby-proxy-host", () => "127.0.0.1",
    "Host to listen for lobby connections on");
var lobbyProxyPortOption = new Option<uint>("--lobby-proxy-port", () => 44994,
    "Port to listen for lobby connections on");
var zoneProxyHostOption = new Option<string>("--zone-proxy-host", () => "127.0.0.1",
    "Host to listen for zone connections on");
var zoneProxyPortOption = new Option<uint>("--zone-proxy-port", () => 44992,
    "Port to listen for zone connections on");
var publicZoneHostOption = new Option<string?>("--public-zone-host",
    "Host to send to the client for zone connections");
var publicZonePortOption = new Option<int?>("--public-zone-port",
    "Port to send to the client for zone connections");
var oodlePathOption = new Option<string>("--oodle-path", () => "oodle-network-shared.dll",
    "Path to the Oodle library to use for compression");
var outputArgument = new Argument<string>("output",
    () => "./captures/" + captureId + ".cfcap",
    "Output file to write captured packets to");

if (!Directory.Exists("captures")) Directory.CreateDirectory("captures");

rootCommand.AddOption(origHostOption);
rootCommand.AddOption(origPortOption);
rootCommand.AddOption(lobbyProxyHostOption);
rootCommand.AddOption(lobbyProxyPortOption);
rootCommand.AddOption(zoneProxyHostOption);
rootCommand.AddOption(zoneProxyPortOption);
rootCommand.AddOption(publicZoneHostOption);
rootCommand.AddOption(publicZonePortOption);
rootCommand.AddOption(oodlePathOption);
rootCommand.AddArgument(outputArgument);

rootCommand.SetHandler(
    Handle,
    new CommandArgumentsBinder {
        OrigHost = origHostOption,
        OrigPort = origPortOption,
        LobbyProxyHost = lobbyProxyHostOption,
        LobbyProxyPort = lobbyProxyPortOption,
        ZoneProxyHost = zoneProxyHostOption,
        ZoneProxyPort = zoneProxyPortOption,
        PublicZoneHost = publicZoneHostOption,
        PublicZonePort = publicZonePortOption,
        OodlePath = oodlePathOption,
        Output = outputArgument
    }
);
await rootCommand.InvokeAsync(args);
return;

async Task<IPAddress> Lookup(string host) {
    return IPAddress.TryParse(host, out var addr) ? addr : (await Dns.GetHostAddressesAsync(host))[0];
}

async Task Handle(CommandArguments arguments) {
    var origHost = await Lookup(arguments.OrigHost);
    var lobbyProxyHost = await Lookup(arguments.LobbyProxyHost);
    var zoneProxyHost = await Lookup(arguments.ZoneProxyHost);

    var lobbyProxy = new LobbyProxy(origHost, arguments.OrigPort, lobbyProxyHost, arguments.LobbyProxyPort);
    var oodle = new OodleLibraryFactory(arguments.OodlePath);
    var zoneProxy = new ZoneProxy(oodle, zoneProxyHost, arguments.ZoneProxyPort,
        arguments.PublicZoneHost != null ? await Lookup(arguments.PublicZoneHost) : null,
        arguments.PublicZonePort != null ? (uint) arguments.PublicZonePort : null);
    lobbyProxy.ZoneProxy = zoneProxy;

    var writer = new CaptureWriter(arguments.Output);
    writer.WriteVersionInfo(new VersionInfo {
        WriterIdentifier = "TemporalStasis.Chronofoil",
        WriterVersion = Assembly.GetExecutingAssembly().GetName().Version!.ToString()
    });
    writer.WriteCaptureStart(captureId, DateTime.UtcNow);

    void WritePacket(ConnectionType protocol, Direction direction, RawInterceptedPacket raw) {
        // Fake a packet header for the capture file
        // This is ***NOT TO SPEC*** but I'm too lazy to make changes to TemporalStasis to expose it
        var header = new PacketHeader {
            Timestamp = (ulong) DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Size = (uint) (Marshal.SizeOf<PacketHeader>() + raw.SegmentHeader.Size + raw.Data.Length),
            ConnectionType = protocol,
            Count = 1
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        ms.WriteStruct(header);
        ms.WriteStruct(raw.SegmentHeader);
        ms.Write(raw.Data);

        writer.AppendCaptureFrame((Protocol) protocol, direction, ms.ToArray());
    }

    lobbyProxy.OnRawClientboundPacket += (
        int _, ref RawInterceptedPacket packet, ref bool _, ConnectionType type
    ) => WritePacket(type, Direction.Rx, packet);
    lobbyProxy.OnRawServerboundPacket += (
        int _, ref RawInterceptedPacket packet, ref bool _, ConnectionType type
    ) => WritePacket(type, Direction.Tx, packet);
    zoneProxy.OnRawClientboundPacket += (
        int _, ref RawInterceptedPacket packet, ref bool _, ConnectionType type
    ) => WritePacket(type, Direction.Rx, packet);
    zoneProxy.OnRawServerboundPacket += (
        int _, ref RawInterceptedPacket packet, ref bool _, ConnectionType type
    ) => WritePacket(type, Direction.Tx, packet);

    await Task.WhenAll(lobbyProxy.StartAsync(), zoneProxy.StartAsync(), Task.Run(() => {
        Console.WriteLine("Listening for connections... press any key to stop");
        Console.ReadKey();
        Console.WriteLine("Stopping...");
        writer.WriteCaptureEnd(DateTime.UtcNow);
        Environment.Exit(0);
    }));
}

public class CommandArguments {
    public required string OrigHost;
    public required uint OrigPort;
    public required string LobbyProxyHost;
    public required uint LobbyProxyPort;
    public required string ZoneProxyHost;
    public required uint ZoneProxyPort;
    public string? PublicZoneHost;
    public int? PublicZonePort;
    public required string OodlePath;
    public required string Output;
}

public class CommandArgumentsBinder : BinderBase<CommandArguments> {
    public required Option<string> OrigHost;
    public required Option<uint> OrigPort;
    public required Option<string> LobbyProxyHost;
    public required Option<uint> LobbyProxyPort;
    public required Option<string> ZoneProxyHost;
    public required Option<uint> ZoneProxyPort;
    public required Option<string?> PublicZoneHost;
    public required Option<int?> PublicZonePort;
    public required Option<string> OodlePath;
    public required Argument<string> Output;

    protected override CommandArguments GetBoundValue(BindingContext bindingContext) {
        var res = bindingContext.ParseResult;
        return new CommandArguments {
            OrigHost = res.GetValueForOption(OrigHost)!,
            OrigPort = res.GetValueForOption(OrigPort),
            LobbyProxyHost = res.GetValueForOption(LobbyProxyHost)!,
            LobbyProxyPort = res.GetValueForOption(LobbyProxyPort),
            ZoneProxyHost = res.GetValueForOption(ZoneProxyHost)!,
            ZoneProxyPort = res.GetValueForOption(ZoneProxyPort),
            PublicZoneHost = res.GetValueForOption(PublicZoneHost),
            PublicZonePort = res.GetValueForOption(PublicZonePort),
            OodlePath = res.GetValueForOption(OodlePath)!,
            Output = res.GetValueForArgument(Output)
        };
    }
}
