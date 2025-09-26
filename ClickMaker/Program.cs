using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

string? configFilePath;
if (args.Length == 0)
{
    Console.WriteLine("Provide the path to the config file:");

    configFilePath = Console.ReadLine();

    if (configFilePath is null)
    {
        Console.WriteLine("Console does not support reading config file.");
        return;
    }
}
else
{
    configFilePath  = args[0];
}

Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

if (!File.Exists(configFilePath))
{
    var dummyConfig = new SoundConfigProfile(
        [
            new(
                MidiFilePath: "Example1.mid",
                ImportantBars: [],
                PerBarSpecialHandling: null,
                GlobalSpecialHandling: null,
                SpecialCallout: [],
                RestartCallout: [],
                CustomStopAt: null,
                BarNumberToRehearsalMark: null,
                BypassQuantizationInPrepare: null,
                Handle68AsGroupOf3InPrepare: null
            ),
            new(
                MidiFilePath: "Example2.mid",
                ImportantBars: [1, 3],
                PerBarSpecialHandling: new() { { "100", [SpecialHandling.Handle68AsGroupOf3] } },
                GlobalSpecialHandling: [SpecialHandling.Handle68AsGroupOf3],
                SpecialCallout: [5, 6, 8],
                RestartCallout: new() { { 1, 2 }, { 5, 1 } },
                CustomStopAt: 10,
                BarNumberToRehearsalMark: new() { { 2, "A" }, { 5, "AA" } },
                BypassQuantizationInPrepare: true,
                Handle68AsGroupOf3InPrepare: true
            )
        ]
    );
    var dummyJson = JsonSerializer.Serialize(dummyConfig, SerializerContext.Default.SoundConfigProfile);
    File.WriteAllText(configFilePath, dummyJson);
    var schema = SerializerContext.Default.SoundConfigProfile.GetJsonSchemaAsNode().ToJsonString();
    File.WriteAllText("Config.schema.json", schema);
    Console.WriteLine($"Config file not found. Dummy config file & schema file have been created at {Path.GetFullPath(configFilePath)}");
    return;
}

var configJson = File.ReadAllText(configFilePath);
var config = JsonSerializer.Deserialize(configJson, SerializerContext.Default.SoundConfigProfile);

if (config.Configs.Length == 0)
    return;

var cache = new Dictionary<CacheHeader, AudioFileReaderAllocation>();
var reportBuilder = new StringBuilder();
string? firstFileDir = null;

foreach (var soundConfig in config.Configs)
{
    var midiFileName = Path.GetFileNameWithoutExtension(soundConfig.MidiFilePath);
    firstFileDir ??= Path.GetDirectoryName(soundConfig.MidiFilePath);
    reportBuilder.AppendLine(midiFileName);
    var clicks = CreateClickInfo(soundConfig.MidiFilePath);
    var barInfo = AnalyzeClickInfo(clicks, soundConfig.RestartCallout ?? []);

    for (var i = 0; i < barInfo.Count - 1; i++)
    {
        var current = barInfo[i];
        var next = barInfo[i + 1];
        barInfo[i] = new(current.Type,
            current.BarNumber,
            current.ClickInfos,
            current.VoiceCountStartClick,
            next.ClickInfos[0].Microsecond,
            current.IsMerged);
    }
    
    var last = barInfo[^1];
    barInfo[^1] = new(last.Type,
        last.BarNumber,
        last.ClickInfos,
        last.VoiceCountStartClick,
        last.ClickInfos[^1].Microsecond + (ulong)last.AverageBeatIntervalMicroseconds,
        last.IsMerged);
    
    var barConfig = soundConfig.PerBarSpecialHandling?.ToDictionary(x => BarRange.Parse(x.Key), x => x.Value) ?? [];
    var specialCallouts = soundConfig.SpecialCallout ?? [];
    barInfo = SanitizeBarInfo(barInfo,
        barConfig,
        soundConfig.GlobalSpecialHandling ?? [],
        soundConfig.CustomStopAt ?? -1,
        soundConfig.BypassQuantizationInPrepare ?? false
    );
    CreateAudioTrack(
        reportBuilder,
        CollectionsMarshal.AsSpan(barInfo),
        soundConfig.ImportantBars.AsSpan(),
        specialCallouts,
        soundConfig.MidiFilePath,
        soundConfig.BarNumberToRehearsalMark ?? [],
        soundConfig.Handle68AsGroupOf3InPrepare ?? false,
        cache
    );
    
    reportBuilder.AppendLine();
}

File.WriteAllText(Path.Combine(firstFileDir ?? ".", "Report.txt"), reportBuilder.ToString());

foreach (var allocation in cache.Values) allocation.Dispose();
cache.Clear();

return;

static List<ClickInfo> CreateClickInfo(string midiFilePath)
{
    var sourceMidiFile = MidiFile.Read(midiFilePath);
    var sourceTempoMap = sourceMidiFile.GetTempoMap();

    var maxMidiClicks = sourceMidiFile.Chunks
        .OfType<TrackChunk>()
        .SelectMany(trackChunk => trackChunk.GetNotes())
        .Max(note => note.Time);

    var timeSigChange = sourceTempoMap
        .GetTimeSignatureChanges()
        .Select(x => new TimeSignatureEvent(x.Time, x.Value))
        .ToList();

    if (timeSigChange.Count == 0 || timeSigChange[0].MidiTime != 0)
        timeSigChange.Insert(0, new(0, TimeSignature.Default));
    
    var timeQueue = new Queue<TimeSignatureEvent>(timeSigChange);
    var clickInfos = new List<ClickInfo>();
    var currentTimeSignature = timeQueue.Dequeue().TimeSignature;
    var oneBeatMusicalLength = GetOneBeatMusicalLength(currentTimeSignature);

    var oneBeatMetricLength = LengthConverter.ConvertTo<MetricTimeSpan>(oneBeatMusicalLength, 0, sourceTempoMap);
    
    for (var i = 0; i < currentTimeSignature.Numerator * Constants.PrepareTimes; i++)
    {
        clickInfos.Add(new((ulong)(i * oneBeatMetricLength.TotalMicroseconds),
            i % currentTimeSignature.Numerator == 0 ? ClickType.PreparePrimary : ClickType.PrepareSecondary));
    }

    var offsetMicroseconds = oneBeatMetricLength.TotalMicroseconds * currentTimeSignature.Numerator * Constants.PrepareTimes;
    long currentMidiTime = 0;
    var remainingBeats = currentTimeSignature.Numerator;
    while (currentMidiTime < maxMidiClicks)
    {
        clickInfos.Add(new((ulong)(TimeConverter.ConvertTo<MetricTimeSpan>(currentMidiTime, sourceTempoMap).TotalMicroseconds + offsetMicroseconds),
            remainingBeats == currentTimeSignature.Numerator
                ? ClickType.Primary
                : ClickType.Secondary));

        remainingBeats--;

        if (remainingBeats <= 0) remainingBeats = currentTimeSignature.Numerator;

        currentMidiTime += LengthConverter.ConvertFrom(
            oneBeatMusicalLength,
            currentMidiTime,
            sourceTempoMap
        );
        
        if (timeQueue.TryPeek(out var newTime) && newTime.MidiTime <= currentMidiTime)
        {
            currentTimeSignature = timeQueue.Dequeue().TimeSignature;
            oneBeatMusicalLength = GetOneBeatMusicalLength(currentTimeSignature);
            remainingBeats = currentTimeSignature.Numerator;
        }
    }
    
    var lastClickMicrosecond = TimeConverter.ConvertTo<MetricTimeSpan>(currentMidiTime, sourceTempoMap).TotalMicroseconds + offsetMicroseconds;
    clickInfos.Add(new((ulong)lastClickMicrosecond, ClickType.Final));

    return clickInfos;
}

static MusicalTimeSpan GetOneBeatMusicalLength(TimeSignature timeSignature) => new(1, timeSignature.Denominator);

static List<BarInfo> AnalyzeClickInfo(List<ClickInfo> clickInfos, Dictionary<int, int> restartCallout)
{
    var bars = new List<BarInfo>();
    var currentBarType = BarType.Prepare;
    var currentBarNumber = 0;
    var currentBarClicks = new List<ClickInfo>();
    foreach (var click in clickInfos)
    {
        switch (click.Type)
        {
            case ClickType.PreparePrimary:
                if (currentBarType != BarType.Prepare)
                    throw new InvalidOperationException("PreparePrimary click found after normal bar started.");
                if (currentBarClicks.Count > 0)
                {
                    bars.Add(new(Type: currentBarType,
                        BarNumber: currentBarNumber,
                        ClickInfos: currentBarClicks.ToArray(),
                        VoiceCountStartClick: currentBarNumber == Constants.PrepareTimes ? 1 : -1,
                        NextBarFirstClickMicrosecond: 0,
                        IsMerged: false
                    ));
                    currentBarClicks.Clear();
                }

                currentBarNumber++;
                break;
            case ClickType.PrepareSecondary:
                if (currentBarType != BarType.Prepare)
                    throw new InvalidOperationException("PrepareSecondary click found after normal bar started.");
                break;
            case ClickType.Primary:
                if (currentBarType == BarType.Prepare)
                {
                    if (currentBarClicks.Count > 0)
                    {
                        bars.Add(new(Type: currentBarType,
                            BarNumber: currentBarNumber,
                            ClickInfos: currentBarClicks.ToArray(),
                            VoiceCountStartClick: currentBarNumber == Constants.PrepareTimes ? 1 : -1,
                            NextBarFirstClickMicrosecond: 0,
                            IsMerged: false
                        ));
                        currentBarClicks.Clear();
                    }

                    currentBarType = BarType.Normal;
                    currentBarNumber = 1;
                }
                else
                {
                    if (currentBarClicks.Count > 0)
                    {
                        bars.Add(new(Type: currentBarType,
                            BarNumber: currentBarNumber,
                            ClickInfos: currentBarClicks.ToArray(),
                            VoiceCountStartClick: restartCallout.GetValueOrDefault(currentBarNumber, -1),
                            NextBarFirstClickMicrosecond: 0,
                            IsMerged: false
                        ));
                        currentBarClicks.Clear();
                    }

                    currentBarNumber++;
                }

                break;
            case ClickType.Secondary:
                if (currentBarType == BarType.Prepare)
                    throw new InvalidOperationException("Secondary click found before normal bar started.");
                break;
            case ClickType.Final:
                continue;
            default:
                throw new UnreachableException();
        }

        currentBarClicks.Add(click);
    }

    return bars;
}

static List<BarInfo> SanitizeBarInfo(
    List<BarInfo> barInfos,
    Dictionary<BarRange, SpecialHandling[]> perBarSpecialHandling,
    SpecialHandling[] globalSpecialHandling,
    int specialStopAt,
    bool bypassQuantizationInPrepare)
{
    if (barInfos.Count == 0) return barInfos;
    var sanitized = new List<BarInfo>();
    var barSpecialHandling = new HashSet<SpecialHandling>();
    foreach (var barInfo in barInfos)
    {
        barSpecialHandling.Clear();
        barSpecialHandling.UnionWith(globalSpecialHandling);
        foreach (var data in perBarSpecialHandling)
        {
            if (!data.Key.IsInRange(barInfo.BarNumber, barInfo.Type == BarType.Prepare)) continue;
            barSpecialHandling.UnionWith(data.Value);
        }
        
        if (barSpecialHandling.Contains(SpecialHandling.Handle68AsGroupOf3) && barInfo.ClickInfos.Length == 6)
        {
            var clickInfo = barInfo.ClickInfos[3];
            barInfo.ClickInfos[3] = 
                clickInfo with
                {
                    Type = 
                    barInfo.Type == BarType.Prepare
                        ? ClickType.PreparePrimary
                        : ClickType.Primary
                };
        }

        if (specialStopAt == barInfo.BarNumber)
        {
            sanitized.Add(new(Type: barInfo.Type,
                BarNumber: barInfo.BarNumber,
                ClickInfos: [barInfo.ClickInfos[0]],
                VoiceCountStartClick: barInfo.VoiceCountStartClick,
                NextBarFirstClickMicrosecond: barInfo.NextBarFirstClickMicrosecond,
                IsMerged: barInfo.IsMerged
            ));
            break;
        }

        if (barSpecialHandling.Contains(SpecialHandling.ByPassQuantization) 
            || bypassQuantizationInPrepare && barInfo.Type == BarType.Prepare
            || TimeSpan.FromMicroseconds(barInfo.AverageBeatIntervalMicroseconds).TotalSeconds > 0.3)
        {
            sanitized.Add(barInfo);
            continue;
        }

        ReadOnlySpan<int> takeIndex = barInfo.ClickInfos.Length switch
        {
            18 => [0, 3, 6, 9, 12, 15],
            12 => [0, 3, 6, 9],
            9 => [0, 3, 6],
            6 => [0, 3],
            4 => [0, 2],
            3 => [0],
            _ => throw new InvalidOperationException($"Unexpected number of clicks in a bar: {barInfo}")
        };

        var clicks = new ClickInfo[takeIndex.Length];
        for (var i = 0; i < takeIndex.Length; i++)
            clicks[i] = barInfo.ClickInfos[takeIndex[i]];

        sanitized.Add(new(barInfo.Type,
            barInfo.BarNumber,
            clicks,
            barInfo.VoiceCountStartClick,
            barInfo.NextBarFirstClickMicrosecond,
            true
        ));
    }
    return sanitized;
}

static void CreateAudioTrack(
    StringBuilder reportBuilder,
    ReadOnlySpan<BarInfo> barInfo,
    ReadOnlySpan<int> importantBarNumber,
    ReadOnlySpan<int> specialCalloutBarNumber,
    string dstPath,
    Dictionary<int, string> barNumberToRehearsalMark,
    bool handle68AsGroupOf3InPrepare,
    Dictionary<CacheHeader, AudioFileReaderAllocation> cache)
{
    var clickSequencer = new WaveSequencer();
    var voiceSequencer = new WaveSequencer();
    var calloutSet = new HashSet<int> { 1, 2, 3, 4 };
    var importantSet = new HashSet<int>();
    foreach (var bar in importantBarNumber)
    {
        importantSet.Add(bar);
        calloutSet.Add(bar - 2);
        calloutSet.Add(bar - 1);
        calloutSet.Add(bar);
        calloutSet.Add(bar + 1);
        calloutSet.Add(bar + 2);
    }

    foreach (var bar in specialCalloutBarNumber)
    {
        importantSet.Add(bar);
        calloutSet.Add(bar);
    }

    const string primaryClickPath = "ClickPrimary.wav";
    const string secondaryClickPath = "ClickSecondary.wav";
    
    foreach (var bar in barInfo)
    {
        switch (bar.Type)
        {
            case BarType.Prepare:
            {
                for (var index = 0; index < bar.ClickInfos.Length; index++)
                {
                    var clickInfo = bar.ClickInfos[index];
                    var barMicrosecondClick = (long)clickInfo.Microsecond;
                    clickSequencer.AddSample(new(clickInfo.Type is ClickType.PreparePrimary or ClickType.Primary ? primaryClickPath : secondaryClickPath),
                        barMicrosecondClick + Constants.GlobalVoiceOffset);
                    if(bar.VoiceCountStartClick == -1) continue;
                    var currentClickNumber = index + 1;
                    if(currentClickNumber < bar.VoiceCountStartClick) continue;
                    var clickDuration = index == bar.ClickInfos.Length - 1
                        ? (long)bar.NextBarFirstClickMicrosecond - barMicrosecondClick
                        : (long)bar.ClickInfos[index + 1].Microsecond - barMicrosecondClick;
                    var clickNumber = currentClickNumber - bar.VoiceCountStartClick + 1;
                    if (bar.ClickInfos.Length == 6 && handle68AsGroupOf3InPrepare && clickNumber > 3) clickNumber -= 3;
                    voiceSequencer.AddSample(GetNumberVoice(clickNumber, bar.IsMerged ? Constants.UseShortVoiceThresholdMicroseconds : clickDuration, barNumberToRehearsalMark, out var extraOffset, cache), barMicrosecondClick + extraOffset);
                }
                break;
            }
            case BarType.Normal:
            {
                for (var index = 0; index < bar.ClickInfos.Length; index++)
                {
                    var clickInfo = bar.ClickInfos[index];
                    var barMicrosecondClick = (long)clickInfo.Microsecond;
                    clickSequencer.AddSample(new(clickInfo.Type is ClickType.PreparePrimary or ClickType.Primary ? primaryClickPath : secondaryClickPath), barMicrosecondClick + Constants.GlobalVoiceOffset);
                    if (index == 0 && calloutSet.Contains(bar.BarNumber))
                    {
                        if(importantSet.Contains(bar.BarNumber))
                            reportBuilder.Append(bar.BarNumber).Append(": ").Append(TimeSpan.FromMicroseconds(barMicrosecondClick).ToString(@"mm\:ss")).AppendLine();
                        var callOutClickDuration = bar.ClickInfos.Length > 1
                            ? (long)bar.ClickInfos[1].Microsecond - barMicrosecondClick
                            : (long)bar.NextBarFirstClickMicrosecond - barMicrosecondClick;
                        voiceSequencer.AddSample(GetNumberVoice(bar.BarNumber, bar.IsMerged ? Constants.UseShortVoiceThresholdMicroseconds : callOutClickDuration, barNumberToRehearsalMark, out var callOutExtraOffset, cache), barMicrosecondClick + callOutExtraOffset);
                    }
                    if(bar.VoiceCountStartClick == -1) continue;
                    var currentClickNumber = index + 1;
                    if(currentClickNumber < bar.VoiceCountStartClick) continue;
                    var clickDuration = index == bar.ClickInfos.Length - 1
                        ? (long)bar.NextBarFirstClickMicrosecond - barMicrosecondClick
                        : (long)bar.ClickInfos[index + 1].Microsecond - barMicrosecondClick;
                    voiceSequencer.AddSample(GetNumberVoice(currentClickNumber - bar.VoiceCountStartClick + 1, bar.IsMerged ? Constants.UseShortVoiceThresholdMicroseconds : clickDuration, barNumberToRehearsalMark, out var extraOffset, cache), barMicrosecondClick + extraOffset);
                }  
                break;
            }
            default:
                throw new UnreachableException();
        }
    }

    var clickFile = Path.ChangeExtension(dstPath, ".click.wav");
    var voiceFile = Path.ChangeExtension(dstPath, ".voice.wav");
    WaveFileWriter.CreateWaveFile16(clickFile, clickSequencer.Bake());
    WaveFileWriter.CreateWaveFile16(voiceFile, voiceSequencer.Bake());
    
    var mixed = new MixingSampleProvider([
        new AudioFileReader(clickFile),
        new AudioFileReader(voiceFile)
    ]);
    
    WaveFileWriter.CreateWaveFile16(Path.ChangeExtension(dstPath, ".clickvoice.wav"), mixed);
}

static AudioFileReader GetNumberVoice(int number, long beatLength, Dictionary<int, string> numberToRehearsalMark, out long offset, Dictionary<CacheHeader, AudioFileReaderAllocation> cachedAllocations)
{
    ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 999);
    ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);

    CacheHeader cacheHeader;
    byte mode;

    const byte useRehearsalMark = 0;
    const byte useFastVoice = 1;
    const byte useSlowVoice = 2;
    
    if(numberToRehearsalMark.TryGetValue(number, out var rehearsalMark))
    {
        cacheHeader = new(rehearsalMark);
        mode = useRehearsalMark;
    }
    else
    { 
        var useFastVariant = beatLength <= Constants.LongVoiceThresholdMicroseconds;
        cacheHeader = new(useFastVariant ? $"F{number}" : $"S{number}");
        mode = useFastVariant ? useFastVoice : useSlowVoice;
    }

    AudioFileReader reader;
    
    if (cachedAllocations.TryGetValue(cacheHeader, out var allocation))
        reader = allocation.CreateReader();
    else
    {
        var path = Path.Combine(
            "VoiceBank",
            mode switch
            {
                useRehearsalMark => "RehearsalMark",
                useFastVoice => "Fast",
                useSlowVoice => "Slow",
                _ => throw new UnreachableException()
            },
            mode switch
            {
                useRehearsalMark => $"{rehearsalMark!}.mp3",
                _ => $"{number:000}.mp3"
            }
        );
        allocation = new(path);
        cachedAllocations[cacheHeader] = allocation;
        reader = allocation.CreateReader();
    }

    offset = Constants.GetVoiceExtraOffset(cacheHeader);

    return reader;
}

readonly record struct CacheHeader(string Data);

readonly record struct BarRange(int From, int To, bool IsPre)
{
    public bool IsInRange(int barNumber, bool isPrepare) => 
        isPrepare == IsPre && barNumber >= From && barNumber <= To;
    
    public static BarRange Parse(string text)
    {
        var isPre = text.StartsWith("Pre");
        if(isPre) text = text[3..];
        
        var parts = text.Split('-');
        switch (parts.Length)
        {
            case 1:
            {
                var single = int.Parse(parts[0]);
                return new(single, single, isPre);
            }
            case 2:
            {
                var from = int.Parse(parts[0]);
                var to = int.Parse(parts[1]);
                return from > to ? throw new ArgumentException("From must not be greater than To.") : new(from, to, isPre);
            }
            default:
                throw new ArgumentException("Invalid BarRange format.");
        }
    }
}

public readonly struct AudioFileReaderAllocation : IDisposable
{
    private readonly string _tempFilePath;
    private readonly Stack<AudioFileReader> _activeReaders = new();
    
    public AudioFileReaderAllocation(string path)
    {
        var sourceReader = new AudioFileReader(path);
        var tmpDir = Path.GetTempPath();
        _tempFilePath = Path.Combine(tmpDir, $"{Guid.NewGuid()}.wav");
        WaveFileWriter.CreateWaveFile16(_tempFilePath, sourceReader);
        sourceReader.Dispose();
    }
    
    public AudioFileReader CreateReader()
    {
        var reader = new AudioFileReader(_tempFilePath);
        _activeReaders.Push(reader);
        return reader;
    }

    public void Dispose()
    {
        while (_activeReaders.TryPop(out var reader)) 
            reader.Dispose();
        if(!File.Exists(_tempFilePath)) return;
        File.Delete(_tempFilePath);
    }
}

public enum ClickType
{
    PreparePrimary,
    PrepareSecondary,
    Primary,
    Secondary,
    Final,
}

readonly record struct ClickInfo(
    ulong Microsecond,
    ClickType Type
);

enum BarType
{
    Prepare,
    Normal,
}

readonly record struct BarInfo(
    BarType Type,
    int BarNumber,
    ClickInfo[] ClickInfos,
    int VoiceCountStartClick,
    ulong NextBarFirstClickMicrosecond,
    bool IsMerged
)
{
    public long AverageBeatIntervalMicroseconds =>
        ClickInfos.Length > 1 
            ? ((long)ClickInfos[^1].Microsecond - (long)ClickInfos[0].Microsecond) / (ClickInfos.Length - 1) 
            : 0;
    public override string ToString() => $"{Type}[{BarNumber}]:{ClickInfos.Length}x{TimeSpan.FromMicroseconds(AverageBeatIntervalMicroseconds).TotalSeconds:00.00}";
}

record struct TimeSignatureEvent(long MidiTime, TimeSignature TimeSignature);

record struct SoundConfig(
    string MidiFilePath,
    int[] ImportantBars,
    Dictionary<string, SpecialHandling[]>? PerBarSpecialHandling,
    SpecialHandling[]? GlobalSpecialHandling,
    int[]? SpecialCallout,
    Dictionary<int, int>? RestartCallout,
    int? CustomStopAt,
    Dictionary<int, string>? BarNumberToRehearsalMark,
    bool? BypassQuantizationInPrepare,
    bool? Handle68AsGroupOf3InPrepare
);

enum SpecialHandling
{
    Handle68AsGroupOf3,
    ByPassQuantization,
}

record struct SoundConfigProfile(SoundConfig[] Configs);

[JsonSerializable(typeof(SoundConfigProfile))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
partial class SerializerContext : JsonSerializerContext;

class WaveSequencer
{
    private record struct Sample(AudioFileReader Provider, long MicrosecondPosition);
    private readonly List<Sample> _samples = [];
    
    public void AddSample(AudioFileReader sample, long microsecondPosition)
    {
        _samples.Add(new(sample, microsecondPosition));
    }

    public MixingSampleProvider Bake()
    {
        _samples.Sort((a, b) => Comparer<long>.Default.Compare(a.MicrosecondPosition, b.MicrosecondPosition));
        var tracks = new List<AudioTrack>();
        
        foreach (var sample in _samples)
        {
            var placed = false;
            foreach (var track in tracks)
            {
                if (!track.TryAppendSample(sample.Provider, sample.MicrosecondPosition)) continue;
                placed = true;
                break;
            }

            if (placed) continue;
            var newTrack = new AudioTrack();
            if (!newTrack.TryAppendSample(sample.Provider, sample.MicrosecondPosition))
            {
                throw new UnreachableException();
            }
            tracks.Add(newTrack);
        }

        return new(tracks
            .Select(track => track.CreateSampleProvider())
            .ToList());
    }

    private class AudioTrack
    {
        private long _headMicrosecondPosition;
        private readonly List<ISampleProvider> _samples = [];

        public bool TryAppendSample(AudioFileReader provider, long requestedStartPosition)
        {
            var offset = requestedStartPosition - _headMicrosecondPosition;
            if (offset < 0) return false;
            var offsetProvider = new OffsetSampleProvider(provider)
            {
                DelayBy = TimeSpan.FromMicroseconds(offset)
            };
            _samples.Add(offsetProvider);
            _headMicrosecondPosition += offset;
            _headMicrosecondPosition += (long)provider.TotalTime.TotalMicroseconds;
            return true;
        }

        public ConcatenatingSampleProvider CreateSampleProvider() => new(_samples);
    }
}

static class Constants
{
    public const int PrepareTimes = 2;
    public const int GlobalVoiceOffset = 100_000;
    public const int UseShortVoiceThresholdMicroseconds = 0;
    public const int LongVoiceThresholdMicroseconds = 600_000;
    public static long GetVoiceExtraOffset(CacheHeader header) =>
        header.Data switch
        {
            "S3" => -60_000,
            "S4" => -100_000,
            "F4" => -50_000,
            _ => 0
        };
}