// GameNetworkManager.cs
// 역할: Steamworks API와 Netcode를 연결하여 로비 생성, 참가, 검색 등 핵심 기능을 수행합니다.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Unity.Netcode;
using Steamworks;
using Steamworks.Data;
using Netcode.Transports.Facepunch;
using UnityEngine.SceneManagement;

public class GameNetworkManager : MonoBehaviour
{
    public static GameNetworkManager instance;
    
    public Lobby? CurrentLobby { get; private set; }
    public static string DisconnectReason { get; private set; } = "";
    
    private readonly List<ulong> bannedSteamIds = new List<ulong>();
    
    [Header("Settings")]
    [SerializeField] private int maxPlayers = 4;
    
    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.L))
        {
            for (int i = 0; i < bannedSteamIds.Count; i++)
            {
                Debug.Log(i+". "+bannedSteamIds[i]);
            }
        }
    }

    private void OnEnable()
    {
        SteamMatchmaking.OnLobbyCreated += OnLobbyCreated;
        SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
        SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeave;
        SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
        
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }
    
    private void OnDisable()
    {
        SteamMatchmaking.OnLobbyCreated -= OnLobbyCreated;
        SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
        SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeave;
        SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
        
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }
    
    public void AddBannedPlayer(ulong steamId)
    {
        if (!bannedSteamIds.Contains(steamId))
        {
            bannedSteamIds.Add(steamId);
        }
    }

    public async void CreateLobby(string lobbyName, LobbyType lobbyType)
    {
        try
        {
            Debug.Log("Creating lobby...");
            var currentLobby = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

            if (!currentLobby.HasValue)
            {
                Debug.LogError("Lobby creation failed.");
                return;
            }

            Lobby lobby = currentLobby.Value;
            lobby.SetData("Name", lobbyName);
            
            switch (lobbyType)
            {
                case LobbyType.Private:
                    lobby.SetPrivate();
                    break;
                case LobbyType.FriendsOnly:
                    lobby.SetFriendsOnly();
                    break;
                case LobbyType.Public:
                    lobby.SetPublic();
                    break;
            }
            
            lobby.SetJoinable(true);

            Debug.Log($"Lobby created! ID: {lobby.Id}, Name: {lobbyName}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"An exception occurred while creating lobby: {e.Message}");
        }
    }

    public async void JoinLobby(Lobby lobby)
    {
        if (await lobby.Join() != RoomEnter.Success)
        {
            Debug.LogError($"Failed to join lobby {lobby.Id}");
        }
    }

    public async void JoinLobbyWithID(ulong lobbyId)
    {
        SteamMatchmaking.JoinLobbyAsync(lobbyId);
    }
    
    public void Disconnect()
    {
        if (string.IsNullOrEmpty(DisconnectReason))
        {
            DisconnectReason = "You have disconnected from the lobby.";
        }
        
        CurrentLobby?.Leave();
        CurrentLobby = null;
        
        if (NetworkManager.Singleton.IsConnectedClient || NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();
        }
        
        PlayerDataManager.instance.ClearAllData();
        SceneManager.LoadScene("LobbySetupScene");
    }
    
    public static void SetDisconnectReason(string reason)
    {
        DisconnectReason = reason;
    }

    /// <summary>
    /// 게임 시작 시 로비를 잠가 새로운 플레이어의 참가를 막습니다.
    /// </summary>
    public void LockLobby()
    {
        if (NetworkManager.Singleton.IsHost && CurrentLobby.HasValue)
        {
            CurrentLobby.Value.SetJoinable(false);
            Debug.Log("Lobby has been locked. No new players can join.");
        }
    }
    
    private void OnLobbyCreated(Result result, Lobby lobby)
    {
        if (result == Result.OK)
        {
            NetworkManager.Singleton.StartHost();
        }
        else Disconnect();
    }

    private void OnLobbyEntered(Lobby lobby)
    {
        CurrentLobby = lobby;

        // 호스트가 아닌 클라이언트의 경우, 여기서 Netcode 클라이언트를 시작합니다.
        if (!NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.gameObject.GetComponent<FacepunchTransport>().targetSteamId = lobby.Owner.Id;
            NetworkManager.Singleton.StartClient();
        }

        // 씬을 로드하고, 로드가 완료되면 환영 메시지를 표시하는 코루틴을 시작합니다.
        StartCoroutine(LoadLobbySceneAndNotify());
    }
    
    private async void OnGameLobbyJoinRequested(Lobby lobby, SteamId steamId)
    {
        JoinLobby(lobby);
    }
    
    private void OnClientConnected(ulong clientId)
    {
        if (clientId == NetworkManager.Singleton.LocalClientId)
        {
            NetworkTransmission.instance.AnnounceMyselfToServerRpc(SteamClient.SteamId, SteamClient.Name);
        }
    }
    
    private IEnumerator LoadLobbySceneAndNotify()
    {
        // LobbyScene을 비동기적으로 로드합니다.
        var loadOperation = SceneManager.LoadSceneAsync("LobbyScene");

        // 씬 로드가 완료될 때까지 대기합니다.
        while (!loadOperation.isDone)
        {
            yield return null;
        }

        // 씬 로드 후 모든 객체의 Awake, Start 함수가 호출되도록 한 프레임을 더 기다립니다. (안정성 확보)
        yield return new WaitForEndOfFrame();
        
        // 이제 ChatManager.instance가 확실히 존재하므로, 안전하게 메시지를 추가합니다.
        ChatManager.instance?.AddMessage("You have joined the lobby.", MessageType.PersonalSystem);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        var playerInfo = PlayerDataManager.instance.GetPlayerInfo(clientId);

        if (NetworkManager.Singleton.IsHost) // 호스트: 떠난 클라이언트를 모두에게 알림
        {
            if (playerInfo != null)
                NetworkTransmission.instance.RemovePlayerClientRpc(clientId, playerInfo.SteamName);
        }
        else // 클라이언트: 연결이 끊겼으므로 메인 메뉴로
        {
            Disconnect();
        }
    }
    
    private void OnLobbyMemberLeave(Lobby lobby, Friend friend) { /* Netcode's OnClientDisconnected handles this */ }
}