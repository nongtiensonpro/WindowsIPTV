using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using IptvApp.Core.Models;

namespace IptvApp.Core.Services;

public class XmltvParser
{
    public static List<EpgProgram> Parse(TextReader textReader)
    {
        var programs = new List<EpgProgram>();
        using var reader = XmlReader.Create(textReader);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
            {
                var program = new EpgProgram();
                program.ChannelId = reader.GetAttribute("channel") ?? string.Empty;

                var startAttr = reader.GetAttribute("start");
                var stopAttr = reader.GetAttribute("stop");

                if (startAttr != null) program.StartTime = ParseXmltvDate(startAttr);
                if (stopAttr != null) program.EndTime = ParseXmltvDate(stopAttr);

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.Name == "title")
                        {
                            program.Title = reader.ReadElementContentAsString();
                        }
                        else if (reader.Name == "desc")
                        {
                            program.Description = reader.ReadElementContentAsString();
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "programme")
                    {
                        break;
                    }
                }

                programs.Add(program);
            }
        }

        return programs;
    }

    private static DateTime ParseXmltvDate(string dateStr)
    {
        try
        {
            if (dateStr.Length >= 14)
            {
                var cleanDate = dateStr.Substring(0, 14);
                if (DateTime.TryParseExact(cleanDate, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    return dt;
                }
            }
        }
        catch
        {
            // Fallback
        }
        return DateTime.MinValue;
    }
}
