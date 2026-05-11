using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DungeonRunners.Core;

namespace DungeonRunners.Networking
{
    public static class MegaCatcher
    {
        private static HashSet<ushort> _mEids = new HashSet<ushort>();
        private static HashSet<ushort> _mCids = new HashSet<ushort>();
        private static int _totalTCP, _totalUDP, _totalChannel, _monsterHits;
        private static Dictionary<string, int> _tcpTypes = new Dictionary<string, int>();
        private static Dictionary<string, int> _udpTypes = new Dictionary<string, int>();
        private static Dictionary<string, int> _chTypes = new Dictionary<string, int>();
        private static Dictionary<string, int> _streamSubs = new Dictionary<string, int>();
        private static Dictionary<string, int> _compSubs = new Dictionary<string, int>();
        private static List<string> _monsterMsgs = new List<string>();
        private static float _lastReport;
        private static bool _active;
        private static bool Enabled => ServerSettings.GetBool("verboseMonsterWireLogging", false) || ServerSettings.GetBool("verbosePacketLogging", false);

        private static void Inc(Dictionary<string, int> d, string k)
        {
            if (d.ContainsKey(k)) d[k]++; else d[k] = 1;
        }

        public static void RegisterMonster(ushort eid, ushort behId, ushort sklId, ushort manId, ushort modId)
        {
            if (!Enabled) return;
            _mEids.Add(eid);
            _mCids.Add(behId); _mCids.Add(sklId); _mCids.Add(manId); _mCids.Add(modId);
            _active = true;
            Debug.LogError($"[MC] Registered eid={eid} cids=[{behId},{sklId},{manId},{modId}]");
        }

        public static void OnRawTCP(byte[] data)
        {
            if (!Enabled) return;
            if (!_active || data == null || data.Length < 1 || data[0] == 0x02) return;
            _totalTCP++;
            Inc(_tcpTypes, $"0x{data[0]:X2}");
            ScanForMonsterIds(data, $"TCP-0x{data[0]:X2}");
        }

        public static void OnChannel(byte ch, byte type, byte[] data)
        {
            if (!Enabled) return;
            if (!_active || data == null) return;
            _totalChannel++;
            Inc(_chTypes, $"ch{ch}/0x{type:X2}");
            ScanForMonsterIds(data, $"ch{ch}/0x{type:X2}");
            if (ch == 7 && type == 0x07 && data.Length > 0) ParseStream(data);
            if (ch == 7 && (type == 0x34 || type == 0x35) && data.Length >= 3)
            {
                ushort cid = (ushort)(data[0] | (data[1] << 8));
                byte sub = data[2];
                Inc(_compSubs, $"cid={cid}/0x{sub:X2}");
                if (_mCids.Contains(cid))
                    _monsterMsgs.Add($"CH-COMP cid={cid} sub=0x{sub:X2} len={data.Length} hex={Hex(data)}");
            }
        }

        public static void OnUDP(byte[] data)
        {
            if (!Enabled) return;
            if (!_active || data == null || data.Length < 1) return;
            _totalUDP++;
            Inc(_udpTypes, $"0x{data[0]:X2}");
            ScanForMonsterIds(data, $"UDP-0x{data[0]:X2}");
            if (data[0] == 0x07 && data.Length > 1)
            {
                byte[] inner = new byte[data.Length - 1];
                Array.Copy(data, 1, inner, 0, inner.Length);
                ParseStream(inner);
            }
            if ((data[0] == 0x34 || data[0] == 0x35) && data.Length >= 4)
            {
                ushort cid = (ushort)(data[1] | (data[2] << 8));
                byte sub = data[3];
                Inc(_compSubs, $"UDP-cid={cid}/0x{sub:X2}");
                if (_mCids.Contains(cid))
                    _monsterMsgs.Add($"UDP-COMP cid={cid} sub=0x{sub:X2} len={data.Length} hex={Hex(data)}");
            }
        }

        private static void ParseStream(byte[] data)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                byte sub = data[pos];
                if (sub == 0x06 || sub == 0x46) break;
                Inc(_streamSubs, $"0x{sub:X2}");
                if ((sub == 0x34 || sub == 0x35) && pos + 3 < data.Length)
                {
                    ushort cid = (ushort)(data[pos + 1] | (data[pos + 2] << 8));
                    byte csub = data[pos + 3];
                    Inc(_compSubs, $"s-cid={cid}/0x{csub:X2}");
                    if (_mCids.Contains(cid))
                        _monsterMsgs.Add($"S-COMP cid={cid} sub=0x{csub:X2} @{pos} hex={Hex(data, pos, 20)}");
                }
                if (sub == 0x36 && pos + 3 < data.Length)
                {
                    ushort eid = (ushort)(data[pos + 1] | (data[pos + 2] << 8));
                    if (_mEids.Contains(eid))
                        _monsterMsgs.Add($"S-SYNC eid={eid} @{pos} hex={Hex(data, pos, 10)}");
                }
                if (pos + 2 < data.Length)
                {
                    ushort v = (ushort)(data[pos + 1] | (data[pos + 2] << 8));
                    if (_mCids.Contains(v))
                        _monsterMsgs.Add($"S-0x{sub:X2} MCID={v} @{pos + 1} hex={Hex(data, pos, 20)}");
                    if (_mEids.Contains(v))
                        _monsterMsgs.Add($"S-0x{sub:X2} MEID={v} @{pos + 1} hex={Hex(data, pos, 20)}");
                }
                pos++;
            }
        }

        private static void ScanForMonsterIds(byte[] data, string src)
        {
            for (int i = 0; i < data.Length - 1; i++)
            {
                ushort val = (ushort)(data[i] | (data[i + 1] << 8));
                if (_mCids.Contains(val))
                {
                    _monsterHits++;
                    _monsterMsgs.Add($"CID={val} in {src} @{i} hex={Hex(data, Math.Max(0, i - 2), 24)}");
                }
                if (_mEids.Contains(val))
                {
                    _monsterHits++;
                    _monsterMsgs.Add($"EID={val} in {src} @{i} hex={Hex(data, Math.Max(0, i - 2), 24)}");
                }
            }
        }

        public static void Report()
        {
            if (!Enabled) return;
            if (!_active) return;
            if (Time.time - _lastReport < 3f) return;
            _lastReport = Time.time;
            Debug.LogError("[MC] ═══════════════════════════════════════════════");
            Debug.LogError($"[MC] TCP={_totalTCP} UDP={_totalUDP} CH={_totalChannel} Hits={_monsterHits}");
            Debug.LogError($"[MC] eids=[{string.Join(",", _mEids)}] cids=[{string.Join(",", _mCids)}]");
            if (_tcpTypes.Count > 0) Debug.LogError($"[MC] TCP: {Fmt(_tcpTypes)}");
            if (_udpTypes.Count > 0) Debug.LogError($"[MC] UDP: {Fmt(_udpTypes)}");
            if (_chTypes.Count > 0) Debug.LogError($"[MC] CH: {Fmt(_chTypes)}");
            if (_streamSubs.Count > 0) Debug.LogError($"[MC] STREAM: {Fmt(_streamSubs)}");
            if (_compSubs.Count > 0) Debug.LogError($"[MC] COMP: {Fmt(_compSubs)}");
            if (_monsterMsgs.Count > 0)
            {
                Debug.LogError($"[MC] ═══ MONSTER ({_monsterMsgs.Count}) ═══");
                foreach (var m in _monsterMsgs.Skip(Math.Max(0, _monsterMsgs.Count - 20)))
                    Debug.LogError($"[MC] 🔴 {m}");
            }
            else if (_totalTCP + _totalUDP > 0)
                Debug.LogError("[MC] ⚠️ NO MONSTER MESSAGES! Client not sending or wrong channel/format");
            Debug.LogError("[MC] ═══════════════════════════════════════════════");
        }

        private static string Fmt(Dictionary<string, int> d)
        {
            return string.Join(" | ", d.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}"));
        }

        private static string Hex(byte[] d, int start = 0, int len = 40)
        {
            start = Math.Max(0, Math.Min(start, d.Length));
            len = Math.Min(len, d.Length - start);
            return len <= 0 ? "" : BitConverter.ToString(d, start, len);
        }
    }
}
