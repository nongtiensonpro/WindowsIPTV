using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IptvApp.Core.Models;

namespace IptvApp.Core.Services;

public class M3uParser
{
    public static async Task<List<Channel>> ParseAsync(TextReader reader)
    {
        var channels = new List<Channel>();
        string? line;
        Channel? currentChannel = null;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith("#EXTINF:"))
            {
                currentChannel = new Channel();
                ParseExtInf(trimmedLine, currentChannel);
            }
            else if (currentChannel != null && !trimmedLine.StartsWith("#") && !string.IsNullOrEmpty(trimmedLine))
            {
                currentChannel.StreamUrl = trimmedLine;
                channels.Add(currentChannel);
                currentChannel = null;
            }
        }

        return channels;
    }

    private static void ParseExtInf(string line, Channel channel)
    {
        channel.LogoUrl = ExtractAttribute(line, "tvg-logo");
        channel.GroupName = ExtractAttribute(line, "group-title");

        int commaIndex = line.IndexOf(',');
        if (commaIndex != -1 && commaIndex < line.Length - 1)
        {
            string rawName = line.Substring(commaIndex + 1).Trim();
            
            // Tối ưu tên kênh: Loại bỏ các tiền tố như "001: " hoặc "001 - " (phổ biến trong list IPTV FPT UDP)
            var match = System.Text.RegularExpressions.Regex.Match(rawName, @"^\d+[:\-\.]\s*(.*)");
            if (match.Success)
            {
                channel.Name = match.Groups[1].Value.Trim();
            }
            else
            {
                channel.Name = rawName;
            }
        }
        else
        {
            channel.Name = ExtractAttribute(line, "tvg-name");
        }
        
        // Nếu không có Group Name (như trong list FPT UDP), tự động gán vào nhóm chung
        if (string.IsNullOrEmpty(channel.GroupName))
        {
            channel.GroupName = "Kênh UDP / Mặc định";
        }
    }

    private static string ExtractAttribute(string line, string attributeName)
    {
        string searchKey = $"{attributeName}=\"";
        int startIndex = line.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
        if (startIndex == -1) return string.Empty;

        startIndex += searchKey.Length;
        int endIndex = line.IndexOf('"', startIndex);
        if (endIndex == -1) return string.Empty;

        return line.Substring(startIndex, endIndex - startIndex);
    }
}
