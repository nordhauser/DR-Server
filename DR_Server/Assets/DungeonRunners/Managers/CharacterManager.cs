using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Data;

namespace DungeonRunners.Managers
{
    /// <summary>
    /// Manages all character data and operations
    /// </summary>
    public class CharacterManager : MonoBehaviour
    {
        private static CharacterManager _instance;
        public static CharacterManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("CharacterManager");
                    _instance = go.AddComponent<CharacterManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private Dictionary<uint, Character> _characters = new Dictionary<uint, Character>();
        private Dictionary<uint, List<Character>> _accountCharacters = new Dictionary<uint, List<Character>>();
        private uint _nextCharacterId = 1000;
        private readonly object _lock = new object();

        void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }

        public Character CreateCharacter(uint accountId, string name, byte gender, byte hairStyle, byte hairColor, byte faceStyle, byte skinColor)
        {
            lock (_lock)
            {
                uint charId = _nextCharacterId++;
                
                var character = new Character
                {
                    Id = charId,
                    AccountId = accountId,
                    Name = name,
                    Level = 1,
                    Experience = 0,
                    Gold = 100,
                    Position = new Vector3(100, 0, 100),
                    ZoneId = 1,
                    WorldId = 1,
                    CurrentHP = 100,
                    MaxHP = 100,
                    CurrentMP = 50,
                    MaxMP = 50,
                    Gender = gender,
                    HairStyle = hairStyle,
                    HairColor = hairColor,
                    FaceStyle = faceStyle,
                    SkinColor = skinColor
                };

                _characters[charId] = character;

                if (!_accountCharacters.ContainsKey(accountId))
                {
                    _accountCharacters[accountId] = new List<Character>();
                }
                _accountCharacters[accountId].Add(character);

                Debug.Log($"Created character: {name} (ID: {charId}) for account {accountId}");
                return character;
            }
        }

        public Character GetCharacter(uint characterId)
        {
            lock (_lock)
            {
                return _characters.ContainsKey(characterId) ? _characters[characterId] : null;
            }
        }

        public List<Character> GetAccountCharacters(uint accountId)
        {
            lock (_lock)
            {
                return _accountCharacters.ContainsKey(accountId) 
                    ? new List<Character>(_accountCharacters[accountId]) 
                    : new List<Character>();
            }
        }

        public bool DeleteCharacter(uint characterId)
        {
            lock (_lock)
            {
                if (_characters.TryGetValue(characterId, out Character character))
                {
                    _characters.Remove(characterId);
                    
                    if (_accountCharacters.ContainsKey(character.AccountId))
                    {
                        _accountCharacters[character.AccountId].Remove(character);
                    }
                    
                    Debug.Log($"Deleted character: {character.Name} (ID: {characterId})");
                    return true;
                }
                return false;
            }
        }

        public void UpdateCharacter(Character character)
        {
            lock (_lock)
            {
                if (_characters.ContainsKey(character.Id))
                {
                    _characters[character.Id] = character;
                }
            }
        }
    }
}