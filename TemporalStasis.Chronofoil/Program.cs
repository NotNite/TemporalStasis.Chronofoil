using System.CommandLine;
using System.CommandLine.Binding;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Chronofoil.CaptureFile;
using Chronofoil.CaptureFile.Generated;
using TemporalStasis;
using TemporalStasis.Compression;
using TemporalStasis.Structs;

var captureId = Guid.NewGuid();

var rootCommand = new RootCommand("TemporalStasis.Chronofoil");
var origHostOption = new Option<string>("--host", () => "neolobby02.ffxiv.com",
    "Lobby server to forward (usually neolobbyXX.ffxiv.com for official servers)");
var origPortOption = new Option<int>("--port", () => 54994,
    "Port of the lobby server to forward (usually 54994 for official servers)");
var lobbyProxyHostOption = new Option<string>("--lobby-proxy-host", () => "127.0.0.1",
    "Host to listen for lobby connections on");
var lobbyProxyPortOption = new Option<int>("--lobby-proxy-port", () => 44994,
    "Port to listen for lobby connections on");
var zoneProxyHostOption = new Option<string>("--zone-proxy-host", () => "127.0.0.1",
    "Host to listen for zone connections on");
var zoneProxyPortOption = new Option<int>("--zone-proxy-port", () => 44992,
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

    var lobbyProxy = new LobbyProxy(new IPEndPoint(origHost, arguments.OrigPort), new IPEndPoint(lobbyProxyHost, arguments.LobbyProxyPort));
    var oodle = new OodleLibraryFactory(arguments.OodlePath);
    var zoneProxy = new ZoneProxy(oodle, new IPEndPoint(zoneProxyHost, arguments.ZoneProxyPort),
        arguments.PublicZoneHost != null && arguments.PublicZonePort != null ? new IPEndPoint(await Lookup(arguments.PublicZoneHost), (int) arguments.PublicZonePort) : null);
    lobbyProxy.ZoneProxy = zoneProxy;

    var writer = new CaptureWriter(arguments.Output);
    writer.WriteVersionInfo(new VersionInfo {
        WriterIdentifier = "TemporalStasis.Chronofoil",
        WriterVersion = Assembly.GetExecutingAssembly().GetName().Version!.ToString()
    });
    writer.WriteCaptureStart(captureId, DateTime.UtcNow);

    void WritePacket(ConnectionType protocol, DestinationType direction, PacketFrame frame) {
        var data = new byte[Unsafe.SizeOf<FrameHeader>() + frame.Data.Length];
        MemoryMarshal.Write<FrameHeader>(data, frame.FrameHeader);
        System.Buffer.BlockCopy(frame.Data.ToArray(), 0, data, Unsafe.SizeOf<FrameHeader>(), frame.Data.Length);

        writer.AppendCaptureFrame((Protocol) protocol, direction is DestinationType.Clientbound ? Direction.Rx : Direction.Tx, data);
    }

    lobbyProxy.OnClientConnected += (connection) => {
        connection.OnPacketFrameReceived += (ref PacketFrame packet, DestinationType type, ref bool dropped) => {
            WritePacket(connection.Type ?? ConnectionType.Lobby, type, packet);
        };
    };

    zoneProxy.OnClientConnected += (connection) => {
        connection.OnPacketFrameReceived += (ref PacketFrame packet, DestinationType type, ref bool dropped) => {
            WritePacket(connection.Type ?? ConnectionType.Zone, type, packet);
        };
    };

    await Task.WhenAll(
        Task.Run(async () => {
            try {
                await lobbyProxy.StartAsync();
            } catch (Exception e) {
                Console.WriteLine(e);
            }
        }),
        Task.Run(async () => {
            try {
                await zoneProxy.StartAsync();
            } catch (Exception e) {
                Console.WriteLine(e);
            }
        }),
        Task.Run(() => {
            Console.WriteLine("Listening for connections... press any key to stop");
            Console.ReadKey();
            Console.WriteLine("Stopping...");
            writer.WriteCaptureEnd(DateTime.UtcNow);
            Environment.Exit(0);
        }));
}

public class CommandArguments {
    public required string OrigHost;
    public required int OrigPort;
    public required string LobbyProxyHost;
    public required int LobbyProxyPort;
    public required string ZoneProxyHost;
    public required int ZoneProxyPort;
    public string? PublicZoneHost;
    public int? PublicZonePort;
    public required string OodlePath;
    public required string Output;
}

public class CommandArgumentsBinder : BinderBase<CommandArguments> {
    public required Option<string> OrigHost;
    public required Option<int> OrigPort;
    public required Option<string> LobbyProxyHost;
    public required Option<int> LobbyProxyPort;
    public required Option<string> ZoneProxyHost;
    public required Option<int> ZoneProxyPort;
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
