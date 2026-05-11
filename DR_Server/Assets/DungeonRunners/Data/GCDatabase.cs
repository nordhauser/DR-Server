using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DungeonRunners.Data
{
    // ═══════════════════════════════════════════════════════════════
    // GC DATABASE
    // Loads all .gc files, resolves inheritance, provides lookups.
    // This is the server's source of truth — same data the client reads.
    //
    // Usage:
    //   GCDatabase.Instance.Load("path/to/gc/files");
    //   float wpnDmgPerLevel = GCDatabase.Instance.GlobalKnobs.GetFloat("WeaponDamagePerLevel");
    //   GCNode mob = GCDatabase.Instance.Resolve("creatures.forestCreatures.Warg.Basic.Pup");
    // ═══════════════════════════════════════════════════════════════

    public class GCDatabase
    {
        private static GCDatabase _instance;
        public static GCDatabase Instance => _instance ??= new GCDatabase();

        // All parsed top-level nodes, keyed by filename (== GC object name)
        private Dictionary<string, GCNode> _nodes = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);

        // Full path registry: "Basic.Pup" → GCNode, "melee01.rank1" → GCNode, etc.
        private Dictionary<string, GCNode> _pathRegistry = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);

        // Resolved (flattened) nodes cache — inheritance applied
        private Dictionary<string, GCNode> _resolvedCache = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _pathAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "creatures.whiskers.broodling.Basic.Grunt", "Whisker_BroodlingBase_Grunt" },
            { "creatures.whiskers.blademaster.Basic.Grunt", "Whisker_BlademasterBase_Grunt" }
        };

        // ── Quick Access ──

        public GCNode GlobalKnobs => GetNode("GlobalKnobs");
        public GCNode Tables => GetNode("Tables");

        public bool IsLoaded { get; private set; }
        public int FileCount { get; private set; }
        public int NodeCount => _pathRegistry.Count;

        // ═══════════════════════════════════════════════════════════════
        // LOADING
        // ═══════════════════════════════════════════════════════════════

        public void Load(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                Debug.LogError($"[GCDatabase] Directory not found: {directoryPath}");
                return;
            }

            _nodes.Clear();
            _pathRegistry.Clear();
            _resolvedCache.Clear();

            string[] files = Directory.GetFiles(directoryPath, "*.gc");
            FileCount = files.Length;
            int parseErrors = 0;

            foreach (string file in files)
            {
                try
                {
                    GCNode node = GCParser.ParseFile(file);
                    if (node != null && !string.IsNullOrEmpty(node.Name))
                    {
                        string name = node.Name;
                        _nodes[name] = node;
                        _pathRegistry[name] = node;

                        // Register all nested children as parent.child paths
                        RegisterChildren(name, node);
                    }
                }
                catch (Exception ex)
                {
                    parseErrors++;
                    if (parseErrors <= 10) // Only log first 10
                        Debug.LogError($"[GCDatabase] Parse error in {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            IsLoaded = true;
            Debug.LogError($"[GCDatabase] ═══════════════════════════════════════════════════");
            Debug.LogError($"[GCDatabase] Loaded {FileCount} files, {_pathRegistry.Count} paths registered");
            if (parseErrors > 0)
                Debug.LogError($"[GCDatabase] {parseErrors} parse errors");

            // Validate critical nodes
            if (GlobalKnobs == null)
                Debug.LogError("[GCDatabase] WARNING: GlobalKnobs.gc not found!");
            else
                Debug.LogError($"[GCDatabase] GlobalKnobs: WeaponDamagePerLevel={GlobalKnobs.GetFloat("WeaponDamagePerLevel")}, MeleeDamagePerStrength={GlobalKnobs.GetFloat("MeleeDamagePerStrength")}");

            if (Tables == null)
                Debug.LogError("[GCDatabase] WARNING: Tables.gc not found!");

            Debug.LogError($"[GCDatabase] ═══════════════════════════════════════════════════");
        }

        private void RegisterChildren(string parentPath, GCNode parent)
        {
            foreach (var kvp in parent.Children)
            {
                string childPath = parentPath + "." + kvp.Key;
                _pathRegistry[childPath] = kvp.Value;
                RegisterChildren(childPath, kvp.Value);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // LOOKUP
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Get a raw (unresolved) node by name or path.</summary>
        public GCNode GetNode(string nameOrPath)
        {
            if (_pathRegistry.TryGetValue(nameOrPath, out GCNode node))
                return node;
            return null;
        }

        /// <summary>
        /// Resolve a dotted GC path like "creatures.forestCreatures.Warg.Basic.Pup".
        /// Tries: exact path match → last segment match → partial path match.
        /// </summary>
        public GCNode Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // 1. Exact match in path registry
            if (_pathRegistry.TryGetValue(path, out GCNode exact))
                return exact;

            if (_pathAliases.TryGetValue(path, out string alias) && _pathRegistry.TryGetValue(alias, out GCNode aliased))
                return aliased;

            // 2. Last segment match (e.g. "creatures.base.UnitMelee" → "UnitMelee")
            string lastSegment = path;
            int lastDot = path.LastIndexOf('.');
            if (lastDot >= 0)
                lastSegment = path.Substring(lastDot + 1);

            if (_pathRegistry.TryGetValue(lastSegment, out GCNode byLast))
                return byLast;

            // 3. Try matching last two segments (e.g. "Basic.Pup")
            if (lastDot > 0)
            {
                int prevDot = path.LastIndexOf('.', lastDot - 1);
                if (prevDot >= 0)
                {
                    string lastTwo = path.Substring(prevDot + 1);
                    if (_pathRegistry.TryGetValue(lastTwo, out GCNode byLastTwo))
                        return byLastTwo;
                }
            }

            return null;
        }

        /// <summary>
        /// Get a fully resolved node with inherited properties applied.
        /// Walks the extends chain and merges properties.
        /// </summary>
        public GCNode ResolveWithInheritance(string path)
        {
            if (_resolvedCache.TryGetValue(path, out GCNode cached))
                return cached;

            GCNode node = Resolve(path);
            if (node == null) return null;

            GCNode resolved = FlattenInheritance(node, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _resolvedCache[path] = resolved;
            return resolved;
        }

        private GCNode FlattenInheritance(GCNode node, HashSet<string> visited)
        {
            // Prevent infinite loops
            string key = node.Name + "|" + (node.Extends ?? "");
            if (visited.Contains(key)) return node;
            visited.Add(key);

            // If no parent, return as-is
            if (string.IsNullOrEmpty(node.Extends))
                return node;

            // Find parent
            GCNode parent = Resolve(node.Extends);
            if (parent == null)
                return node;

            // Recursively resolve parent first
            GCNode resolvedParent = FlattenInheritance(parent, visited);

            // Merge: child overrides parent
            var merged = new GCNode
            {
                Name = node.Name,
                Extends = node.Extends,
                IsStatic = node.IsStatic,
                IsAnonymous = node.IsAnonymous,
                SourceFile = node.SourceFile
            };

            // Start with parent properties
            foreach (var kvp in resolvedParent.Properties)
                merged.Properties[kvp.Key] = kvp.Value;
            // Override with child properties
            foreach (var kvp in node.Properties)
                merged.Properties[kvp.Key] = kvp.Value;

            // Merge children: parent children first, child overrides
            foreach (var kvp in resolvedParent.Children)
                merged.Children[kvp.Key] = kvp.Value;
            foreach (var kvp in node.Children)
            {
                if (merged.Children.ContainsKey(kvp.Key))
                {
                    // Deep merge child block — child properties override parent's child
                    merged.Children[kvp.Key] = MergeNodes(merged.Children[kvp.Key], kvp.Value);
                }
                else
                {
                    merged.Children[kvp.Key] = kvp.Value;
                }
            }

            // Anonymous children: concatenate
            merged.AnonymousChildren.AddRange(resolvedParent.AnonymousChildren);
            merged.AnonymousChildren.AddRange(node.AnonymousChildren);

            return merged;
        }

        private GCNode MergeNodes(GCNode parent, GCNode child)
        {
            var merged = new GCNode
            {
                Name = child.Name,
                Extends = child.Extends ?? parent.Extends,
                IsStatic = child.IsStatic || parent.IsStatic,
                SourceFile = child.SourceFile
            };

            foreach (var kvp in parent.Properties) merged.Properties[kvp.Key] = kvp.Value;
            foreach (var kvp in child.Properties) merged.Properties[kvp.Key] = kvp.Value;

            foreach (var kvp in parent.Children) merged.Children[kvp.Key] = kvp.Value;
            foreach (var kvp in child.Children)
            {
                if (merged.Children.ContainsKey(kvp.Key))
                    merged.Children[kvp.Key] = MergeNodes(merged.Children[kvp.Key], kvp.Value);
                else
                    merged.Children[kvp.Key] = kvp.Value;
            }

            return merged;
        }

        // ═══════════════════════════════════════════════════════════════
        // CONVENIENCE: GlobalKnobs typed accessors
        // ═══════════════════════════════════════════════════════════════

        public float GetKnob(string name, float fallback = 0f)
        {
            return GlobalKnobs?.GetFloat(name, fallback) ?? fallback;
        }

        public int GetKnobInt(string name, int fallback = 0)
        {
            return GlobalKnobs?.GetInt(name, fallback) ?? fallback;
        }

        // ═══════════════════════════════════════════════════════════════
        // CONVENIENCE: XP Curve from Tables.gc
        // ═══════════════════════════════════════════════════════════════

        private List<(int level, float value)> _xpCurve;

        public List<(int level, float value)> GetXPCurve()
        {
            if (_xpCurve != null) return _xpCurve;

            _xpCurve = new List<(int, float)>();
            var tables = GetNode("Tables");
            if (tables == null) return _xpCurve;

            var xpTable = tables.GetChild("Experience");
            if (xpTable == null) return _xpCurve;

            foreach (var entry in xpTable.AnonymousChildren)
            {
                int level = entry.GetInt("Level", 0);
                float value = entry.GetFloat("Value", 0f);
                if (level > 0)
                    _xpCurve.Add((level, value));
            }

            _xpCurve.Sort((a, b) => a.level.CompareTo(b.level));
            Debug.LogError($"[GCDatabase] XP Curve loaded: {_xpCurve.Count} entries");
            foreach (var e in _xpCurve)
                Debug.LogError($"[GCDatabase]   Level {e.level}: {e.value} kills");

            return _xpCurve;
        }

        /// <summary>
        /// Interpolate XP curve for a given target level — same as client CurveTable logic.
        /// Returns number of same-level kills needed (before ExperienceMod).
        /// </summary>
        public float InterpolateXPCurve(int targetLevel)
        {
            var curve = GetXPCurve();
            if (curve.Count == 0) return 10f;

            for (int i = 0; i < curve.Count; i++)
            {
                if (targetLevel <= curve[i].level)
                {
                    if (i == 0) return curve[i].value;
                    float t = (float)(targetLevel - curve[i - 1].level) / (curve[i].level - curve[i - 1].level);
                    return curve[i - 1].value + t * (curve[i].value - curve[i - 1].value);
                }
            }
            return curve[curve.Count - 1].value;
        }

        // ═══════════════════════════════════════════════════════════════
        // CONVENIENCE: Weapon properties
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get weapon Description properties from a resolved weapon node.
        /// Walks inheritance to find Damage, DamageVolatility, Range, CoolDown, WeaponClass.
        /// </summary>
        public (float damage, float volatility, float range, float cooldown, string weaponClass)
            GetWeaponStats(string weaponGCPath)
        {
            var node = ResolveWithInheritance(weaponGCPath);
            if (node == null)
                return (1.0f, 0.5f, 8f, 1.75f, "1HMELEE");

            var desc = node.GetChild("Description");
            if (desc == null) desc = node; // Some files put props at top level

            return (
                damage: desc.GetFloat("Damage", 1.0f),
                volatility: desc.GetFloat("DamageVolatility", 0.5f),
                range: desc.GetFloat("Range", 8f),
                cooldown: desc.GetFloat("CoolDown", 1.75f),
                weaponClass: desc.GetString("WeaponClass", "1HMELEE")
            );
        }

        // ═══════════════════════════════════════════════════════════════
        // CONVENIENCE: Creature stats (with inheritance)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get creature Description properties from a resolved creature node.
        /// Walks inheritance for AttackRating, DamageMod, DefenseRating, MaxHealth, etc.
        /// </summary>
        public GCNode GetCreatureStats(string creatureGCPath)
        {
            var node = ResolveWithInheritance(creatureGCPath);
            if (node == null) return null;

            // Creature stats live in Description child
            return node.GetChild("Description") ?? node;
        }

        /// <summary>
        /// Get creature weapon/manipulator properties (Damage, Range, CoolDown, etc.)
        /// from the Manipulators.PrimaryWeapon.Description block.
        /// </summary>
        public GCNode GetCreatureWeaponStats(string creatureGCPath)
        {
            var node = ResolveWithInheritance(creatureGCPath);
            if (node == null) return null;

            var manip = node.GetChild("Manipulators");
            if (manip == null) return null;

            var weapon = manip.GetChild("PrimaryWeapon");
            if (weapon == null) return null;

            return weapon.GetChild("Description") ?? weapon;
        }

        // ═══════════════════════════════════════════════════════════════
        // DEBUG: Dump a node tree
        // ═══════════════════════════════════════════════════════════════

        public void DumpNode(string path, int maxDepth = 3)
        {
            var node = Resolve(path);
            if (node == null)
            {
                Debug.LogError($"[GCDatabase] DumpNode: '{path}' not found");
                return;
            }
            DumpNodeRecursive(node, 0, maxDepth);
        }

        private void DumpNodeRecursive(GCNode node, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            string indent = new string(' ', depth * 2);

            Debug.LogError($"{indent}[{node.Name}] extends={node.Extends ?? "none"} src={node.SourceFile}");
            foreach (var kvp in node.Properties)
                Debug.LogError($"{indent}  {kvp.Key} = {kvp.Value}");
            foreach (var kvp in node.Children)
                DumpNodeRecursive(kvp.Value, depth + 1, maxDepth);
            foreach (var anon in node.AnonymousChildren)
                DumpNodeRecursive(anon, depth + 1, maxDepth);
        }
    }
}
