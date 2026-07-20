using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using InteropGenerator.Runtime;
using Meddle.Plugin.Models;
using Meddle.Utils.Files.SqPack;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.UI;

public class RsfTab : ITab
{
    private readonly SqPack pack;
    private readonly ILogger<RsfTab> logger;

    public RsfTab(SqPack pack, ILogger<RsfTab> logger)
    {
        this.pack = pack;
        this.logger = logger;
    }
    
    public string Name => "RSF";
    public int Order => 10;
    public MenuType MenuType => MenuType.Debug;
    private string inputData = "";
    
    public unsafe void Draw()
    {
        var world = LayoutWorld.Instance();
        if (world == null)
        {
            ImGui.Text("LayoutWorld instance is null.");
            return;
        }

        var rsf = world->RsfMap;
        var rsvMap = world->RsvMap;

        DrawRsf(rsf);
        DrawSqPackRsf();
        DrawRsv(rsvMap);

        DrawControls(rsf);
    }
    
    private void DrawSqPackRsf()
    {
        var rsfDumpBuilder = new StringBuilder();
        foreach (var key in pack.RsfData.Keys.ToArray())
        {
            if (!pack.RsfData.TryGetValue(key, out var value))
            {
                ImGui.Text($"Key: {key:X8} - Value not found.");
                continue;
            }
            
            var valueDataHex = BitConverter.ToString(value).Replace("-", " ");
            rsfDumpBuilder.AppendLine($"Key: {key:X8} - Value: {valueDataHex}");
        }

        ImGui.Text("Pack RSF");
        var rsfDump = rsfDumpBuilder.ToString();
        ImGui.InputTextMultiline("##PackRSFDump", ref rsfDump, 100000, flags: ImGuiInputTextFlags.ReadOnly);
    }
    
    private unsafe void DrawControls(StdMap<ulong, Pointer<byte>>* rsf)
    {
        ImGui.Text("Input RSF");
        ImGui.InputTextMultiline("##RsfInput", ref inputData, 100000);
        if (ImGui.Button("Load Rsf"))
        {
            var rsfLines = inputData.ReplaceLineEndings("\n").Split("\n");
            foreach (var rsfLine in rsfLines)
            {
                var segments = rsfLine.Split('-');
                if (segments.Length != 2)
                {
                    continue;
                }
                var key = segments[0].Trim()["Key: ".Length..];
                var value = segments[1].Trim()["Value: ".Length..];
                var rsfBytes = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();
                var keyUlong = Convert.ToUInt64(key, 16);
                var valueDataBuffer = new byte[64];
                rsfBytes.CopyTo(valueDataBuffer);
                var alloc = Marshal.AllocHGlobal(valueDataBuffer.Length);
                Marshal.Copy(valueDataBuffer, 0, alloc, valueDataBuffer.Length);
                rsf->Add(keyUlong, (Pointer<byte>)alloc.ToPointer());
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear Rsf"))
        {
            foreach (var key in rsf->Keys.ToArray())
            {
                rsf->Remove(key);
            }
        }
    }
    private static unsafe void DrawRsf(StdMap<ulong, Pointer<byte>>* rsf)
    {
        var rsfDumpBuilder = new StringBuilder();
        foreach (var key in rsf->Keys)
        {
            if (!rsf->TryGetValuePointer(key, out var value))
            {
                ImGui.Text($"Key: {key:X8} - Value not found.");
                continue;
            }
            
            if (value == null)
            {
                ImGui.Text($"Key: {key:X8} - Value is null.");
                continue;
            }

            var valueData = value->Value;
            if (valueData == null)
            {
                ImGui.Text($"Key: {key:X8} - Value data is null.");
                continue;
            }
            
            // byte* to byte[64]
            var valueDataBuffer = new byte[64];
            Marshal.Copy((nint)valueData, valueDataBuffer, 0, 64);
            // write to builder as hexadecimal
            var valueDataHex = BitConverter.ToString(valueDataBuffer).Replace("-", " ");
            rsfDumpBuilder.AppendLine($"Key: {key:X8} - Value: {valueDataHex}");
        }

        ImGui.Text("Live RSF");
        var rsfDump = rsfDumpBuilder.ToString();
        ImGui.InputTextMultiline("##RSFDump", ref rsfDump, 100000, flags: ImGuiInputTextFlags.ReadOnly);
    }
    private static unsafe void DrawRsv(StdMap<Utf8String, CStringPointer>* rsvMap)
    {
        var rsvDumpBuilder = new StringBuilder();
        foreach (var key in rsvMap->Keys)
        {
            if (!rsvMap->TryGetValue(key, out var value, false))
            {
                ImGui.Text($"Key: {key:X8} - Value not found.");
                continue;
            }

            var valueStr = value.ToString();
            rsvDumpBuilder.AppendLine($"Key: {key:X8} - Value: {valueStr}");
        }
        
        ImGui.Text("Live RSV");
        var rsvDump = rsvDumpBuilder.ToString();
        ImGui.InputTextMultiline("##RsvDump", ref rsvDump, 100000, flags: ImGuiInputTextFlags.ReadOnly);
    }
    
    public void Dispose()
    {
    }
}
