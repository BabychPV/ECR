# tools/hse401/Hse401Xlsx.Lib.ps1
# Читання книги HSE401 (xlsm) як zip з XML: значення (кеш Excel `<v>`), ознака формули `<f>`, текст формули.
# Файл ТІЛЬКИ читається (копія в каталозі, який передає викликач); VBA не запускається і не читається.
# Python не потрібен. Windows PowerShell 5.1 і PowerShell 7.

Set-StrictMode -Version 2.0

if (-not ('Hse401.XlsxReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Hse401 {
    public class Cell {
        public string Ref;
        public int Row;
        public int Col;         // 1-based
        public string Value;    // кеш Excel (для t="s" - розгорнутий рядок), null якщо порожньо
        public string Type;     // s | str | n | b | e | inlineStr | (null = n)
        public bool IsFormula;
        public string Formula;  // текст формули (null для shared-слейвів)
    }

    public static class XlsxReader {
        public static int ColIndex(string letters) {
            int n = 0;
            foreach (char ch in letters) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            return n;
        }
        public static string ColLetters(int idx) {
            string s = "";
            while (idx > 0) { int m = (idx - 1) % 26; s = (char)('A' + m) + s; idx = (idx - 1) / 26; }
            return s;
        }

        static string ReadText(XmlReader si) {
            var sb = new System.Text.StringBuilder();
            bool inRph = false; int rphDepth = 0;
            while (si.Read()) {
                if (si.NodeType == XmlNodeType.Element && si.LocalName == "rPh") { inRph = true; rphDepth = si.Depth; }
                else if (si.NodeType == XmlNodeType.EndElement && si.LocalName == "rPh" && si.Depth == rphDepth) inRph = false;
                else if (!inRph && si.NodeType == XmlNodeType.Element && si.LocalName == "t") sb.Append(si.ReadElementContentAsString());
            }
            return sb.ToString();
        }

        public static List<string> ReadSharedStrings(ZipArchive z) {
            var list = new List<string>();
            var e = z.GetEntry("xl/sharedStrings.xml");
            if (e == null) return list;
            using (var s = e.Open())
            using (var r = XmlReader.Create(s)) {
                while (r.Read()) {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") {
                        using (var sub = r.ReadSubtree()) list.Add(ReadText(sub));
                    }
                }
            }
            return list;
        }

        public static List<Cell> ReadSheet(ZipArchive z, string entryName, List<string> shared) {
            var res = new List<Cell>();
            var e = z.GetEntry(entryName);
            if (e == null) throw new FileNotFoundException(entryName);
            using (var s = e.Open())
            using (var r = XmlReader.Create(s)) {
                while (r.Read()) {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "c") continue;
                    var c = new Cell();
                    c.Ref = r.GetAttribute("r");
                    c.Type = r.GetAttribute("t");
                    int i = 0;
                    while (i < c.Ref.Length && char.IsLetter(c.Ref[i])) i++;
                    c.Col = ColIndex(c.Ref.Substring(0, i));
                    c.Row = int.Parse(c.Ref.Substring(i));
                    if (!r.IsEmptyElement) {
                        using (var sub = r.ReadSubtree()) {
                            sub.Read();   // сам <c>
                            sub.Read();
                            while (!sub.EOF) {
                                if (sub.NodeType != XmlNodeType.Element) { sub.Read(); continue; }
                                if (sub.LocalName == "f") {
                                    c.IsFormula = true;
                                    bool empty = sub.IsEmptyElement;
                                    string t = empty ? null : sub.ReadElementContentAsString();
                                    if (empty) sub.Read();
                                    if (!string.IsNullOrEmpty(t)) c.Formula = t;
                                } else if (sub.LocalName == "v") {
                                    c.Value = sub.ReadElementContentAsString();
                                } else if (sub.LocalName == "is") {
                                    using (var ss = sub.ReadSubtree()) c.Value = ReadText(ss);
                                    sub.Read();
                                } else sub.Read();
                            }
                        }
                    }
                    if (c.Type == "s" && c.Value != null) c.Value = shared[int.Parse(c.Value)];
                    res.Add(c);
                }
            }
            return res;
        }
    }
}
'@ -ReferencedAssemblies 'System.IO.Compression', 'System.Xml' -ErrorAction Stop
}

function Open-Hse401Zip {
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    return [System.IO.Compression.ZipFile]::OpenRead($Path)
}

function Get-Hse401Cells {
    <# Комірки аркуша (xl/worksheets/sheetN.xml) як List[Hse401.Cell]. #>
    param([Parameter(Mandatory)]$Zip, [Parameter(Mandatory)][string]$Entry, [Parameter(Mandatory)]$Shared)
    return ,[Hse401.XlsxReader]::ReadSheet($Zip, $Entry, $Shared)
}
