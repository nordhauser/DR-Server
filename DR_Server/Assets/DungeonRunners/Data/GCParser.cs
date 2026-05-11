using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DungeonRunners.Data
{
    // ═══════════════════════════════════════════════════════════════
    // GC FILE PARSER
    // Parses Dungeon Runners .gc file format into GCNode trees.
    // Handles: comments, extends, static, anonymous (*), nested blocks,
    //          key=value properties, quoted strings
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents one node in a GC object tree.
    /// A node has a name, optional parent (extends), properties, and children.
    /// </summary>
    public class GCNode
    {
        public string Name { get; set; }
        public string Extends { get; set; }  // full dotted path from extends clause
        public bool IsStatic { get; set; }
        public bool IsAnonymous { get; set; } // * extends ...

        // Flat properties: Key = Value
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Named child blocks
        public Dictionary<string, GCNode> Children { get; set; } = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);

        // Anonymous child blocks (* extends ...)
        public List<GCNode> AnonymousChildren { get; set; } = new List<GCNode>();

        // Source file for debugging
        public string SourceFile { get; set; }

        // ── Property Accessors ──

        public string GetString(string key, string fallback = "")
        {
            return Properties.TryGetValue(key, out string val) ? val : fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                // Handle values like ".5" or "0.5" or "100"
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                    return result;
            }
            return fallback;
        }

        public int GetInt(string key, int fallback = 0)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                if (int.TryParse(val, out int result))
                    return result;
                // Try as float and truncate
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return (int)f;
            }
            return fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            if (Properties.TryGetValue(key, out string val))
            {
                return val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       val.Equals("1", StringComparison.Ordinal);
            }
            return fallback;
        }

        public bool HasProperty(string key) => Properties.ContainsKey(key);
        public bool HasChild(string name) => Children.ContainsKey(name);
        public GCNode GetChild(string name) => Children.TryGetValue(name, out GCNode c) ? c : null;

        /// <summary>
        /// Walk a dotted path to find a deeply nested child.
        /// e.g. "Description.Damage" → Children["Description"].Properties["Damage"]
        /// Returns the deepest node that matches.
        /// </summary>
        public GCNode ResolvePath(string dottedPath)
        {
            if (string.IsNullOrEmpty(dottedPath)) return this;

            string[] parts = dottedPath.Split('.');
            GCNode current = this;

            foreach (string part in parts)
            {
                if (current.Children.TryGetValue(part, out GCNode child))
                {
                    current = child;
                }
                else
                {
                    return null;
                }
            }
            return current;
        }

        /// <summary>
        /// Get a property from a nested path. e.g. GetNestedProperty("Description", "Damage")
        /// </summary>
        public string GetNestedProperty(string childName, string propName)
        {
            var child = GetChild(childName);
            if (child != null && child.Properties.TryGetValue(propName, out string val))
                return val;
            return null;
        }

        public float GetNestedFloat(string childName, string propName, float fallback = 0f)
        {
            string val = GetNestedProperty(childName, propName);
            if (val != null && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                return result;
            return fallback;
        }

        public int GetNestedInt(string childName, string propName, int fallback = 0)
        {
            string val = GetNestedProperty(childName, propName);
            if (val != null)
            {
                if (int.TryParse(val, out int result)) return result;
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return (int)f;
            }
            return fallback;
        }

        public override string ToString()
        {
            return $"GCNode[{Name}] extends={Extends ?? "none"} props={Properties.Count} children={Children.Count}";
        }
    }

    /// <summary>
    /// Parser for .gc file format. Handles comments, blocks, properties, extends.
    /// </summary>
    public static class GCParser
    {
        public static GCNode ParseFile(string filePath)
        {
            string text = File.ReadAllText(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            return Parse(text, fileName);
        }

        public static GCNode Parse(string text, string sourceFile = "")
        {
            text = StripComments(text);
            int pos = 0;
            var node = ParseTopLevel(text, ref pos, sourceFile);
            return node;
        }

        // ── Comment stripping ──

        private static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            bool inString = false;

            while (i < text.Length)
            {
                // Track quoted strings
                if (text[i] == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inString = !inString;
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                if (inString)
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                // Line comment
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                // Block comment
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    if (i + 1 < text.Length) i += 2; // skip */
                    continue;
                }

                sb.Append(text[i]);
                i++;
            }

            return sb.ToString();
        }

        // ── Top-level parsing ──
        // Format: Name [extends Parent] { ... }

        private static GCNode ParseTopLevel(string text, ref int pos, string sourceFile)
        {
            SkipWhitespace(text, ref pos);

            // Read the declaration line up to '{'
            string name = null;
            string extends_ = null;
            bool isStatic = false;

            // Check for 'static' keyword
            if (TryReadWord(text, ref pos, out string firstWord))
            {
                if (firstWord.Equals("static", StringComparison.OrdinalIgnoreCase))
                {
                    isStatic = true;
                    SkipWhitespace(text, ref pos);
                    TryReadWord(text, ref pos, out name);
                }
                else
                {
                    name = firstWord;
                }
            }

            if (name == null) return null;

            SkipWhitespace(text, ref pos);

            // Check for 'extends'
            int savedPos = pos;
            if (TryReadWord(text, ref pos, out string maybeExtends))
            {
                if (maybeExtends.Equals("extends", StringComparison.OrdinalIgnoreCase))
                {
                    SkipWhitespace(text, ref pos);
                    TryReadWord(text, ref pos, out extends_);
                }
                else
                {
                    pos = savedPos; // not extends, rewind
                }
            }

            SkipWhitespace(text, ref pos);

            var node = new GCNode
            {
                Name = name,
                Extends = extends_,
                IsStatic = isStatic,
                IsAnonymous = name == "*",
                SourceFile = sourceFile
            };

            // Parse block body if present
            if (pos < text.Length && text[pos] == '{')
            {
                pos++; // skip {
                ParseBlockBody(text, ref pos, node, sourceFile);
            }

            return node;
        }

        // ── Block body parsing ──
        // Contains: Key = Value; pairs and nested blocks

        private static void ParseBlockBody(string text, ref int pos, GCNode parent, string sourceFile)
        {
            while (pos < text.Length)
            {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;

                // End of block
                if (text[pos] == '}')
                {
                    pos++;
                    return;
                }

                // Read first word
                bool isStatic = false;
                if (!TryReadWord(text, ref pos, out string word)) break;

                // Check for 'static' modifier
                if (word.Equals("static", StringComparison.OrdinalIgnoreCase))
                {
                    isStatic = true;
                    SkipWhitespace(text, ref pos);
                    if (!TryReadWord(text, ref pos, out word)) break;
                }

                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;

                // Determine what follows the word
                char next = text[pos];

                if (next == '=')
                {
                    // Property: Key = Value;
                    pos++; // skip =
                    SkipWhitespace(text, ref pos);
                    string value = ReadValue(text, ref pos);
                    parent.Properties[word] = value;
                }
                else if (next == '{' || IsWord(text, pos, "extends"))
                {
                    // Nested block: Name [extends Parent] { ... }
                    string childExtends = null;

                    if (IsWord(text, pos, "extends"))
                    {
                        // Skip "extends"
                        pos += 7;
                        SkipWhitespace(text, ref pos);
                        TryReadWord(text, ref pos, out childExtends);
                        SkipWhitespace(text, ref pos);
                    }

                    var child = new GCNode
                    {
                        Name = word,
                        Extends = childExtends,
                        IsStatic = isStatic,
                        IsAnonymous = word == "*",
                        SourceFile = sourceFile
                    };

                    if (pos < text.Length && text[pos] == '{')
                    {
                        pos++; // skip {
                        ParseBlockBody(text, ref pos, child, sourceFile);
                    }

                    if (child.IsAnonymous)
                    {
                        parent.AnonymousChildren.Add(child);
                    }
                    else
                    {
                        parent.Children[word] = child;
                    }
                }
                else
                {
                    // Could be a standalone identifier on a line (e.g. Name = value without semicolon)
                    // or a malformed entry — skip to next line
                    SkipToNextStatement(text, ref pos);
                }
            }
        }

        // ── Value reading ──

        private static string ReadValue(string text, ref int pos)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) return "";

            var sb = new StringBuilder();

            if (text[pos] == '"')
            {
                // Quoted string
                pos++; // skip opening "
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length)
                    {
                        sb.Append(text[pos + 1]);
                        pos += 2;
                    }
                    else
                    {
                        sb.Append(text[pos]);
                        pos++;
                    }
                }
                if (pos < text.Length) pos++; // skip closing "
            }
            else
            {
                // Unquoted value — read until ; or newline or }
                while (pos < text.Length && text[pos] != ';' && text[pos] != '\n' && text[pos] != '\r' && text[pos] != '}')
                {
                    sb.Append(text[pos]);
                    pos++;
                }
            }

            // Skip trailing semicolon
            if (pos < text.Length && text[pos] == ';') pos++;

            return sb.ToString().Trim();
        }

        // ── Helpers ──

        private static void SkipWhitespace(string text, ref int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }

        private static bool TryReadWord(string text, ref int pos, out string word)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length)
            {
                word = null;
                return false;
            }

            int start = pos;
            // Word characters: letters, digits, underscore, dot (for extends paths), *, :
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '.' || text[pos] == '*' || text[pos] == ':'))
            {
                pos++;
            }

            if (pos == start)
            {
                word = null;
                return false;
            }

            word = text.Substring(start, pos - start);
            return true;
        }

        private static bool IsWord(string text, int pos, string word)
        {
            if (pos + word.Length > text.Length) return false;
            for (int i = 0; i < word.Length; i++)
            {
                if (char.ToLower(text[pos + i]) != char.ToLower(word[i])) return false;
            }
            // Make sure word boundary
            if (pos + word.Length < text.Length && char.IsLetterOrDigit(text[pos + word.Length])) return false;
            return true;
        }

        private static void SkipToNextStatement(string text, ref int pos)
        {
            while (pos < text.Length && text[pos] != ';' && text[pos] != '}' && text[pos] != '{' && text[pos] != '\n')
                pos++;
            if (pos < text.Length && text[pos] == ';') pos++;
        }
    }
}
