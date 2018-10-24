﻿using UnityEngine;
using UnityEngine.UI;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using ModIO;

// TODO(@jackson): Clean up after removing IModBrowserView
// TODO(@jackson): Queue missed requests? (Unsub fail)
// TODO(@jackson): Correct subscription loading
// TODO(@jackson): Add user events
// TODO(@jackson): Error handling on log in
// TODO(@jackson): Update view function names (see FilterView)
public class ModBrowser : MonoBehaviour
{
    // ---------[ NESTED CLASSES ]---------
    [Serializable]
    private class ManifestData
    {
        public int lastCacheUpdate = -1;
    }

    [Serializable]
    private class SortOptionData
    {
        public string dropdownText;
        public string fieldName;
        public bool isSortAscending;

        public static SortOptionData Create(string dropdownText, string fieldName, bool isSortAscending)
        {
            SortOptionData newSOD = new SortOptionData()
            {
                dropdownText = dropdownText,
                fieldName = fieldName,
                isSortAscending = isSortAscending,
            };
            return newSOD;
        }
    }

    [Serializable]
    public class InspectorViewData
    {
        public int currentModIndex;
        public int lastModIndex;
    }

    // ---------[ CONST & STATIC ]---------
    public static string manifestFilePath { get { return CacheClient.GetCacheDirectory() + "browser_manifest.data"; } }
    public static readonly UserProfile GUEST_PROFILE = new UserProfile()
    {
        id = 0,
        username = "Guest",
    };
    private readonly SortOptionData[] sortOptions = new SortOptionData[]
    {
        SortOptionData.Create("NEWEST",         ModIO.API.GetAllModsFilterFields.dateLive, false),
        SortOptionData.Create("POPULARITY",     ModIO.API.GetAllModsFilterFields.popular, true),
        SortOptionData.Create("RATING",         ModIO.API.GetAllModsFilterFields.rating, false),
        SortOptionData.Create("SUBSCRIBERS",    ModIO.API.GetAllModsFilterFields.subscribers, false),
    };

    // ---------[ FIELDS ]---------
    [Header("Settings")]
    public int gameId = 0;
    public string gameAPIKey = string.Empty;
    public bool isAutomaticUpdateEnabled = false;
    public ModBrowserViewMode viewMode = ModBrowserViewMode.Subscriptions;

    [Header("UI Components")]
    public ExplorerView explorerView;
    public SubscriptionsView subscriptionsView;
    public InspectorView inspectorView;
    public InputField nameSearchField;
    public ModTagFilterView tagFilterView;
    public ModTagFilterBar tagFilterBar;
    public Dropdown sortByDropdown;
    public ModBrowserUserDisplay userDisplay;
    public LoginDialog loginDialog;
    public MessageDialog messageDialog;
    public Button prevPageButton;
    public Button nextPageButton;

    [Header("Display Data")]
    public InspectorViewData inspectorData = new InspectorViewData();
    public List<int> subscribedModIds = new List<int>();
    public UserProfile userProfile = null;
    public List<string> filterTags = new List<string>();

    [Header("Runtime Data")]
    public int lastCacheUpdate = -1;
    public string titleSearch = string.Empty;
    public RequestFilter explorerViewFilter = new RequestFilter();
    public List<ModBinaryRequest> modDownloads = new List<ModBinaryRequest>();


    // ---------[ ACCESSORS ]---------
    public void RequestExplorerPage(int pageIndex,
                                    Action<RequestPage<ModProfile>> onSuccess,
                                    Action<WebRequestError> onError)
    {
        // PaginationParameters
        APIPaginationParameters pagination = new APIPaginationParameters();
        pagination.limit = explorerView.ItemCount;
        pagination.offset = pageIndex * explorerView.ItemCount;

        // Send Request
        APIClient.GetAllMods(explorerViewFilter, pagination,
                             onSuccess, onError);
    }

    public void RequestSubscriptionsPage(int pageIndex,
                                         Action<RequestPage<ModProfile>> onSuccess,
                                         Action<WebRequestError> onError)
    {
        int offset = pageIndex * subscriptionsView.TEMP_pageSize;

        RequestPage<ModProfile> modPage = new RequestPage<ModProfile>()
        {
            size = subscriptionsView.TEMP_pageSize,
            resultOffset = offset,
            resultTotal = subscribedModIds.Count,
            items = null,
        };

        int remainingModCount = modPage.resultTotal - offset;
        if(remainingModCount > 0)
        {
            int arraySize = (int)Mathf.Min(modPage.size, remainingModCount);
            modPage.items = new ModProfile[arraySize];

            Action<List<ModProfile>> onGetModProfiles = (list) =>
            {
                list.CopyTo(0, modPage.items, 0, arraySize);

                onSuccess(modPage);
            };

            ModManager.GetModProfiles(subscribedModIds.GetRange(offset, arraySize),
                                      onGetModProfiles, onError);
        }
        else
        {
            modPage.items = new ModProfile[0];

            onSuccess(modPage);
        }
    }


    // ---------[ INITIALIZATION ]---------
    private void Start()
    {
        // load APIClient vars
        #pragma warning disable 0162
        if(this.gameId <= 0)
        {
            if(GlobalSettings.GAME_ID <= 0)
            {
                Debug.LogError("[mod.io] Game ID is missing. Save it to GlobalSettings or this MonoBehaviour before starting the app",
                               this);
                return;
            }

            this.gameId = GlobalSettings.GAME_ID;
        }
        if(String.IsNullOrEmpty(this.gameAPIKey))
        {
            if(String.IsNullOrEmpty(GlobalSettings.GAME_APIKEY))
            {
                Debug.LogError("[mod.io] Game API Key is missing. Save it to GlobalSettings or this MonoBehaviour before starting the app",
                               this);
                return;
            }

            this.gameAPIKey = GlobalSettings.GAME_APIKEY;
        }
        #pragma warning restore 0162

        APIClient.gameId = this.gameId;
        APIClient.gameAPIKey = this.gameAPIKey;
        APIClient.userAuthorizationToken = CacheClient.LoadAuthenticatedUserToken();;

        // assert ui is prepared

        // searchBar.Initialize();
        // searchBar.profileFiltersUpdated += OnProfileFiltersUpdated;

        // initialize login dialog
        loginDialog.gameObject.SetActive(false);
        loginDialog.onSecurityCodeSent += (m) =>
        {
            CloseLoginDialog();
            OpenMessageDialog_OneButton("Security Code Requested",
                                        m.message,
                                        "Back",
                                        () => { CloseMessageDialog(); OpenLoginDialog(); });
        };
        loginDialog.onUserOAuthTokenReceived += (t) =>
        {
            CloseLoginDialog();

            OpenMessageDialog_TwoButton("Login Successful",
                                        "Do you want to merge the local guest account mod subscriptions"
                                        + " with your mod subscriptions on the server?",
                                        "Merge Subscriptions", () => { CloseMessageDialog(); LogUserIn(t, false); },
                                        "Replace Subscriptions", () => { CloseMessageDialog(); LogUserIn(t, true); });
        };
        loginDialog.onAPIRequestError += (e) =>
        {
            CloseLoginDialog();

            OpenMessageDialog_OneButton("Authorization Failed",
                                        e.message,
                                        "Back",
                                        () => { CloseMessageDialog(); OpenLoginDialog(); });
        };

        messageDialog.gameObject.SetActive(false);

        if(userDisplay != null)
        {
            userDisplay.button.onClick.AddListener(OpenLoginDialog);
            userDisplay.profile = ModBrowser.GUEST_PROFILE;
            userDisplay.UpdateUIComponents();
        }

        // load manifest
        ManifestData manifest = CacheClient.ReadJsonObjectFile<ManifestData>(ModBrowser.manifestFilePath);
        if(manifest != null)
        {
            this.lastCacheUpdate = manifest.lastCacheUpdate;
        }

        // load user
        this.userProfile = CacheClient.LoadAuthenticatedUserProfile();
        if(this.userProfile == null)
        {
            this.userProfile = ModBrowser.GUEST_PROFILE;
        }

        this.subscribedModIds = CacheClient.LoadAuthenticatedUserSubscriptions();
        if(this.subscribedModIds == null)
        {
            this.subscribedModIds = new List<int>();
        }

        if(!String.IsNullOrEmpty(APIClient.userAuthorizationToken))
        {
            // callbacks
            Action<UserProfile> onGetUserProfile = (u) =>
            {
                this.userProfile = u;

                if(this.userDisplay != null)
                {
                    this.userDisplay.profile = u;
                    this.userDisplay.UpdateUIComponents();

                    this.userDisplay.button.onClick.RemoveListener(OpenLoginDialog);
                    this.userDisplay.button.onClick.AddListener(LogUserOut);
                }
            };

            // TODO(@jackson): DO BETTER
            Action<RequestPage<ModProfile>> onGetSubscriptions = (r) =>
            {
                this.subscribedModIds = new List<int>(r.items.Length);
                foreach(var modProfile in r.items)
                {
                    this.subscribedModIds.Add(modProfile.id);
                }
                OnSubscriptionsUpdated();
            };

            // requests
            ModManager.GetAuthenticatedUserProfile(onGetUserProfile,
                                                   null);

            RequestFilter filter = new RequestFilter();
            filter.fieldFilters.Add(ModIO.API.GetUserSubscriptionsFilterFields.gameId,
                                    new EqualToFilter<int>(){ filterValue = this.gameId });

            APIClient.GetUserSubscriptions(filter, null, onGetSubscriptions, null);
        }


        // initialize views
        inspectorView.Initialize();
        inspectorView.subscribeButton.onClick.AddListener(() => OnSubscribeButtonClicked(inspectorView.profile));
        inspectorView.gameObject.SetActive(false);
        UpdateInspectorViewPageButtonInteractibility();

        // collectionView.Initialize();
        // collectionView.onUnsubscribeClicked += OnUnsubscribeButtonClicked;
        // collectionView.profileCollection = CacheClient.IterateAllModProfiles().Where(p => subscribedModIds.Contains(p.id));
        // collectionView.gameObject.SetActive(false);

        tagFilterView.selectedTags = this.filterTags;
        tagFilterView.gameObject.SetActive(false);
        tagFilterView.onSelectedTagsChanged += () =>
        {
            if(this.filterTags != tagFilterView.selectedTags)
            {
                this.filterTags = tagFilterView.selectedTags;
            }
            if(tagFilterBar.selectedTags != this.filterTags)
            {
                tagFilterBar.selectedTags = this.filterTags;
            }
            tagFilterBar.UpdateDisplay();

            UpdateFilters();
        };

        tagFilterBar.selectedTags = this.filterTags;
        tagFilterBar.gameObject.SetActive(true);
        tagFilterBar.onSelectedTagsChanged += () =>
        {
            if(this.filterTags != tagFilterBar.selectedTags)
            {
                this.filterTags = tagFilterBar.selectedTags;
            }
            if(tagFilterView.selectedTags != this.filterTags)
            {
                tagFilterView.selectedTags = this.filterTags;
            }
            tagFilterView.UpdateDisplay();

            UpdateFilters();
        };

        ModManager.GetGameProfile((g) =>
                                  {
                                    tagFilterView.categories = g.tagCategories;
                                    tagFilterView.Initialize();

                                    tagFilterBar.categories = g.tagCategories;
                                    tagFilterBar.Initialize();
                                  },
                                  WebRequestError.LogAsWarning);

        // final elements
        // TODO(@jackson): nameSearchField.onValueChanged.AddListener((t) => {});
        nameSearchField.onEndEdit.AddListener((t) =>
        {
            if(Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                UpdateFilters();
            }
        } );

        if(sortByDropdown != null)
        {
            sortByDropdown.options = new List<Dropdown.OptionData>(sortOptions.Count());
            foreach(SortOptionData option in sortOptions)
            {
                sortByDropdown.options.Add(new Dropdown.OptionData() { text = option.dropdownText });
            }
            sortByDropdown.value = 0;
            sortByDropdown.captionText.text = sortOptions[0].dropdownText;

            sortByDropdown.onValueChanged.AddListener((v) => UpdateFilters());
        }

        InitializeExplorerView();
        InitializeSubscriptionsView();
    }

    private void InitializeSubscriptionsView()
    {
        // TODO(@jackson): Update displays on subscribe/unsubscribe
        subscriptionsView.Initialize();

        // TODO(@jackson): Hook up events
        subscriptionsView.inspectRequested += OnExplorerItemClicked;
        subscriptionsView.subscribeRequested += (i) => OnSubscribeButtonClicked(i.profile);
        subscriptionsView.unsubscribeRequested += (i) => OnSubscribeButtonClicked(i.profile);
        subscriptionsView.toggleModEnabledRequested += (i) => ToggleModEnabled(i.profile);


        RequestPage<ModProfile> modPage = new RequestPage<ModProfile>()
        {
            size = subscriptionsView.TEMP_pageSize,
            resultOffset = 0,
            resultTotal = 0,
            items = new ModProfile[0],
        };
        subscriptionsView.currentPage = modPage;

        RequestSubscriptionsPage(0,
                                 (page) =>
                                 {
                                    Debug.Log("Got sub profiles: " + page.items.Length);
                                    if(subscriptionsView.currentPage == modPage)
                                    {
                                        subscriptionsView.currentPage = page;
                                        subscriptionsView.UpdateCurrentPageDisplay();
                                    }
                                },
                                null);

        subscriptionsView.UpdateCurrentPageDisplay();
        subscriptionsView.gameObject.SetActive(false);
    }

    private void InitializeExplorerView()
    {
        explorerView.Initialize();

        explorerView.inspectRequested += OnExplorerItemClicked;
        explorerView.subscribeRequested += (i) => OnSubscribeButtonClicked(i.profile);
        explorerView.unsubscribeRequested += (i) => OnSubscribeButtonClicked(i.profile);
        explorerView.toggleModEnabledRequested += (i) => ToggleModEnabled(i.profile);

        explorerView.subscribedModIds = this.subscribedModIds;

        // setup filter
        explorerViewFilter = new RequestFilter();

        SortOptionData sortOption = sortOptions[0];
        explorerViewFilter.sortFieldName = sortOption.fieldName;
        explorerViewFilter.isSortAscending = sortOption.isSortAscending;


        RequestPage<ModProfile> modPage = new RequestPage<ModProfile>()
        {
            size = explorerView.ItemCount,
            items = new ModProfile[explorerView.ItemCount],
            resultOffset = 0,
            resultTotal = 0,
        };
        explorerView.currentPage = modPage;

        RequestExplorerPage(0,
                            (page) =>
                            {
                                if(explorerView.currentPage == modPage)
                                {
                                    explorerView.currentPage = page;
                                    explorerView.UpdateCurrentPageDisplay();
                                    UpdateExplorerViewPageButtonInteractibility();
                                }
                            },
                            null);

        explorerView.targetPage = null;

        explorerView.UpdateCurrentPageDisplay();
        explorerView.gameObject.SetActive(true);

        UpdateExplorerViewPageButtonInteractibility();
    }

    // ---------[ UPDATES ]---------
    private const float AUTOMATIC_UPDATE_INTERVAL = 15f;
    private bool isUpdateRunning = false;

    private void Update()
    {
        if(this.isAutomaticUpdateEnabled)
        {
            if(!isUpdateRunning
               && (ServerTimeStamp.Now - this.lastCacheUpdate) >= AUTOMATIC_UPDATE_INTERVAL)
            {
                PollForServerUpdates();
            }
        }

        // if(activeDownload != null)
        // {
        //     float downloaded = 0f;
        //     if(activeDownload.webRequest != null)
        //     {
        //         downloaded = activeDownload.webRequest.downloadProgress * 100f;
        //     }

        //     inspectorView.installButtonText.text = "Downloading [" + downloaded.ToString("00.00") + "%]";
        // }
    }

    protected void PollForServerUpdates()
    {
        Debug.Assert(!isUpdateRunning);

        this.isUpdateRunning = true;

        int updateStartTimeStamp = ServerTimeStamp.Now;

        ModManager.FetchAllModEvents(this.lastCacheUpdate, updateStartTimeStamp,
                                     (me) => { this.ProcessUpdates(me, updateStartTimeStamp); },
                                     (e) => { WebRequestError.LogAsWarning(e); this.isUpdateRunning = false; });

        // TODO(@jackson): Add User Events
    }

    protected void ProcessUpdates(List<ModEvent> modEvents, int updateStartTimeStamp)
    {
        if(modEvents != null)
        {
            // - Event Handler Notification -
            Action<List<ModProfile>> onAvailable = (profiles) =>
            {
                // this.OnModsAvailable(profiles);
            };
            Action<List<ModProfile>> onEdited = (profiles) =>
            {
                // this.OnModsEdited(profiles);
            };
            Action<List<ModfileStub>> onReleasesUpdated = (modfiles) =>
            {
                // this.OnModReleasesUpdated(modfiles);
            };
            Action<List<int>> onUnavailable = (ids) =>
            {
                // this.OnModsUnavailable(ids);
            };
            Action<List<int>> onDeleted = (ids) =>
            {
                // this.OnModsDeleted(ids);
            };

            Action onSuccess = () =>
            {
                this.lastCacheUpdate = updateStartTimeStamp;
                this.isUpdateRunning = false;

                ManifestData manifest = new ManifestData()
                {
                    lastCacheUpdate = this.lastCacheUpdate,
                };

                CacheClient.WriteJsonObjectFile(ModBrowser.manifestFilePath, manifest);

                // #if DEBUG
                // if(Application.isPlaying)
                // #endif
                // {
                //     IModBrowserView view = GetViewForMode(this.viewMode);
                //     view.profileCollection = GetFilteredProfileCollectionForMode(this.viewMode);
                //     view.Refresh();
                // }
            };

            Action<WebRequestError> onError = (error) =>
            {
                WebRequestError.LogAsWarning(error);
                this.isUpdateRunning = false;
            };

            ModManager.ApplyModEventsToCache(modEvents,
                                             onAvailable, onEdited,
                                             onUnavailable, onDeleted,
                                             onReleasesUpdated,
                                             onSuccess,
                                             onError);
        }
        else
        {
            this.lastCacheUpdate = updateStartTimeStamp;
            this.isUpdateRunning = false;

            ManifestData manifest = new ManifestData()
            {
                lastCacheUpdate = this.lastCacheUpdate,
            };

            CacheClient.WriteJsonObjectFile(ModBrowser.manifestFilePath, manifest);
        }
    }

    // ---------[ USER CONTROL ]---------
    public void LogUserIn(string oAuthToken, bool clearExistingSubscriptions)
    {
        Debug.Assert(!String.IsNullOrEmpty(oAuthToken),
                     "[mod.io] ModBrowser.LogUserIn requires a valid oAuthToken");


        if(this.userDisplay != null)
        {
            this.userDisplay.button.onClick.RemoveListener(OpenLoginDialog);
            this.userDisplay.button.onClick.AddListener(LogUserOut);
        }

        StartCoroutine(UserLoginCoroutine(oAuthToken, clearExistingSubscriptions));
    }

    private IEnumerator UserLoginCoroutine(string oAuthToken, bool clearExistingSubscriptions)
    {
        bool isRequestDone = false;
        WebRequestError requestError = null;

        // NOTE(@jackson): Could be much improved by not deleting matching mod subscription files
        if(clearExistingSubscriptions)
        {
            foreach(int modId in subscribedModIds)
            {
                // remove from disk
                CacheClient.DeleteAllModfileAndBinaryData(modId);
            }
            CacheClient.ClearAuthenticatedUserSubscriptions();

            subscribedModIds = new List<int>(0);
            subscriptionsView.currentPage = new RequestPage<ModProfile>()
            {
                size = subscriptionsView.TEMP_pageSize,
                items = new ModProfile[0],
                resultOffset = 0,
                resultTotal = 0,
            };
            subscriptionsView.UpdateCurrentPageDisplay();
        }


        // - set APIClient and CacheClient vars -
        APIClient.userAuthorizationToken = oAuthToken;
        CacheClient.SaveAuthenticatedUserToken(oAuthToken);


        // - get the user profile -
        UserProfile requestProfile = null;
        APIClient.GetAuthenticatedUser((p) => { isRequestDone = true; requestProfile = p; },
                                       (e) => { isRequestDone = true; requestError = e; });

        while(!isRequestDone) { yield return null; }

        if(requestError != null)
        {
            throw new System.NotImplementedException();
            // return;
        }

        CacheClient.SaveAuthenticatedUserProfile(requestProfile);
        this.userProfile = requestProfile;
        if(this.userDisplay != null)
        {
            userDisplay.profile = this.userProfile;
            userDisplay.UpdateUIComponents();
        }


        // - get server subscriptions -
        List<int> subscriptionsToPush = new List<int>(subscribedModIds);
        bool allPagesReceived = false;

        RequestFilter subscriptionFilter = new RequestFilter();
        subscriptionFilter.fieldFilters.Add(ModIO.API.GetUserSubscriptionsFilterFields.gameId,
                                            new EqualToFilter<int>() { filterValue = this.gameId });

        APIPaginationParameters pagination = new APIPaginationParameters()
        {
            limit = APIPaginationParameters.LIMIT_MAX,
            offset = 0,
        };

        RequestPage<ModProfile> requestPage = null;
        while(!allPagesReceived)
        {
            isRequestDone = false;
            requestError = null;
            requestPage = null;

            APIClient.GetUserSubscriptions(subscriptionFilter, pagination,
                                           (r) => { isRequestDone = true; requestPage = r; },
                                           (e) => { isRequestDone = true; requestError = e; });

            while(!isRequestDone)
            {
                yield return null;
            }

            if(requestError != null)
            {
                throw new System.NotImplementedException();
                // return?
            }

            CacheClient.SaveModProfiles(requestPage.items);

            foreach(ModProfile profile in requestPage.items)
            {
                if(!subscribedModIds.Contains(profile.id))
                {
                    subscribedModIds.Add(profile.id);

                    // begin download
                    ModBinaryRequest binaryRequest = ModManager.RequestCurrentRelease(profile);

                    if(!binaryRequest.isDone)
                    {
                        binaryRequest.succeeded += (r) =>
                        {
                            Debug.Log(profile.name + " Downloaded!");
                            modDownloads.Remove(binaryRequest);
                        };

                        binaryRequest.failed += (r) =>
                        {
                            Debug.Log(profile.name + " Download Failed!");
                            modDownloads.Remove(binaryRequest);
                        };

                        modDownloads.Add(binaryRequest);
                    }
                }

                subscriptionsToPush.Remove(profile.id);
            }

            allPagesReceived = (requestPage.items.Length < requestPage.size);

            if(!allPagesReceived)
            {
                pagination.offset += pagination.limit;
            }
        }

        CacheClient.SaveAuthenticatedUserSubscriptions(subscribedModIds);


        // - push missing subscriptions -
        foreach(int modId in subscriptionsToPush)
        {
            APIClient.SubscribeToMod(modId,
                                     (p) => Debug.Log("[mod.io] Mod subscription merged: " + p.id + "-" + p.name),
                                     (e) => Debug.Log("[mod.io] Mod subscription merge failed: " + modId + "\n"
                                                      + e.ToUnityDebugString()));
        }
    }

    public void LogUserOut()
    {
        // - clear current user -
        APIClient.userAuthorizationToken = null;
        CacheClient.DeleteAuthenticatedUser();

        foreach(int modId in this.subscribedModIds)
        {
            CacheClient.DeleteAllModfileAndBinaryData(modId);
        }

        // - set up guest account -
        CacheClient.SaveAuthenticatedUserSubscriptions(this.subscribedModIds);

        this.userProfile = ModBrowser.GUEST_PROFILE;
        this.subscribedModIds = new List<int>(0);

        if(this.userDisplay != null)
        {
            this.userDisplay.profile = ModBrowser.GUEST_PROFILE;
            this.userDisplay.UpdateUIComponents();

            this.userDisplay.button.onClick.RemoveListener(LogUserOut);
            this.userDisplay.button.onClick.AddListener(OpenLoginDialog);
        }

        // - clear subscription view -
        RequestPage<ModProfile> modPage = new RequestPage<ModProfile>()
        {
            size = subscriptionsView.TEMP_pageSize,
            resultOffset = 0,
            resultTotal = 0,
            items = new ModProfile[0],
        };
        subscriptionsView.currentPage = modPage;
        subscriptionsView.UpdateCurrentPageDisplay();
    }

    // ---------[ UI CONTROL ]---------
    public void SetViewModeSubscriptions()
    {
        this.viewMode = ModBrowserViewMode.Subscriptions;
        this.UpdateViewMode();
    }
    public void SetViewModeBrowse()
    {
        this.viewMode = ModBrowserViewMode.Explorer;
        this.UpdateViewMode();
    }

    public void UpdateViewMode()
    {
        // IModBrowserView view = GetViewForMode(this.viewMode);
        // if(view.gameObject.activeSelf) { return; }

        // collectionView.gameObject.SetActive(false);
        // explorerView.gameObject.SetActive(false);

        // view.Refresh();
        // view.gameObject.SetActive(true);

        switch(this.viewMode)
        {
            case ModBrowserViewMode.Subscriptions:
            {
                subscriptionsView.gameObject.SetActive(true);
                explorerView.gameObject.SetActive(false);
            }
            break;
            case ModBrowserViewMode.Explorer:
            {
                explorerView.gameObject.SetActive(true);
                subscriptionsView.gameObject.SetActive(false);
            }
            break;
        }
    }

    public void OnExplorerItemClicked(ModBrowserItem item)
    {
        inspectorData.currentModIndex = item.index + explorerView.currentPage.resultOffset;
        Debug.Log("NEW currentModIndex=" + inspectorData.currentModIndex);

        ChangeInspectorPage(0);
    }

    public void CloseInspector()
    {
        inspectorView.gameObject.SetActive(false);
    }

    public void OpenLoginDialog()
    {
        loginDialog.gameObject.SetActive(true);
        loginDialog.Initialize();
    }

    public void CloseLoginDialog()
    {
        loginDialog.gameObject.SetActive(false);
    }

    public void OpenMessageDialog_OneButton(string header, string content,
                                            string buttonText, Action buttonCallback)
    {
        messageDialog.button01.GetComponentInChildren<Text>().text = buttonText;

        messageDialog.button01.onClick.RemoveAllListeners();
        messageDialog.button01.onClick.AddListener(() => buttonCallback());

        messageDialog.button02.gameObject.SetActive(false);

        OpenMessageDialog(header, content);
    }

    public void OpenMessageDialog_TwoButton(string header, string content,
                                            string button01Text, Action button01Callback,
                                            string button02Text, Action button02Callback)
    {
        messageDialog.button01.GetComponentInChildren<Text>().text = button01Text;

        messageDialog.button01.onClick.RemoveAllListeners();
        messageDialog.button01.onClick.AddListener(() => button01Callback());

        messageDialog.button02.GetComponentInChildren<Text>().text = button02Text;

        messageDialog.button02.onClick.RemoveAllListeners();
        messageDialog.button02.onClick.AddListener(() => button02Callback());

        messageDialog.button02.gameObject.SetActive(true);

        OpenMessageDialog(header, content);
    }

    private void OpenMessageDialog(string header, string content)
    {
        messageDialog.header.text = header;
        messageDialog.content.text = content;

        messageDialog.gameObject.SetActive(true);
    }

    private void CloseMessageDialog()
    {
        messageDialog.gameObject.SetActive(false);
    }

    public void OnSubscribeButtonClicked(ModProfile profile)
    {
        Debug.Assert(profile != null);

        if(subscribedModIds.Contains(profile.id))
        {
            // "View In Collection"
            CloseInspector();
            SetViewModeSubscriptions();
        }
        else
        {
            Text buttonText = inspectorView.subscribeButton.GetComponent<Text>();
            if(buttonText == null)
            {
                buttonText = inspectorView.subscribeButton.GetComponentInChildren<Text>();
            }

            if(userProfile.id != ModBrowser.GUEST_PROFILE.id)
            {
                inspectorView.subscribeButton.interactable = false;

                // TODO(@jackson): Protect from switch
                Action<ModProfile> onSubscribe = (p) =>
                {
                    buttonText.text = "View In Collection";
                    inspectorView.subscribeButton.interactable = true;
                    OnSubscribedToMod(p);
                };

                // TODO(@jackson): onError
                Action<WebRequestError> onError = (e) =>
                {
                    Debug.Log("Failed to Subscribe");
                    inspectorView.subscribeButton.interactable = true;
                };

                APIClient.SubscribeToMod(profile.id, onSubscribe, onError);
            }
            else
            {
                buttonText.text = "View In Collection";
                OnSubscribedToMod(profile);
            }
        }
    }

    public void OnSubscribedToMod(ModProfile profile)
    {
        Debug.Assert(profile != null);

        // update collection
        subscribedModIds.Add(profile.id);
        CacheClient.SaveAuthenticatedUserSubscriptions(subscribedModIds);

        // begin download
        ModBinaryRequest request = ModManager.RequestCurrentRelease(profile);

        if(!request.isDone)
        {
            modDownloads.Add(request);

            request.succeeded += (r) =>
            {
                Debug.Log(profile.name + " Downloaded!");
                modDownloads.Remove(request);
            };
        }
    }

    public void OnUnsubscribeButtonClicked(ModProfile modProfile)
    {
        Debug.Assert(modProfile != null);

        if(userProfile.id != ModBrowser.GUEST_PROFILE.id)
        {
            // TODO(@jackson): ???
            // collectionView.unsubscribeButton.interactable = false;

            Action onUnsubscribe = () =>
            {
                OnUnsubscribedFromMod(modProfile);
            };

            // TODO(@jackson): onError
            Action<WebRequestError> onError = (e) =>
            {
                Debug.Log("[mod.io] Failed to Unsubscribe");
                // collectionView.unsubscribeButton.interactable = true;
            };

            APIClient.UnsubscribeFromMod(modProfile.id, onUnsubscribe, onError);
        }
        else
        {
            OnUnsubscribedFromMod(modProfile);
        }
    }

    public void OnUnsubscribedFromMod(ModProfile modProfile)
    {
        Debug.Assert(modProfile != null);
        Debug.LogWarning("UPDATE SUBSCRIPTION DISPLAYS");

        // update collection
        subscribedModIds.Remove(modProfile.id);
        CacheClient.SaveAuthenticatedUserSubscriptions(subscribedModIds);

        // remove from disk
        CacheClient.DeleteAllModfileAndBinaryData(modProfile.id);

        RequestPage<ModProfile> modPage = subscriptionsView.currentPage;

        RequestSubscriptionsPage(modPage.resultOffset,
                                 (page) =>
                                 {
                                    if(subscriptionsView.currentPage == modPage)
                                    {
                                        subscriptionsView.currentPage = page;
                                        subscriptionsView.UpdateCurrentPageDisplay();
                                    }
                                },
                                null);
    }

    public void ChangeExplorerPage(int direction)
    {
        // TODO(@jackson): Queue on isTransitioning?
        if(explorerView.isTransitioning)
        {
            Debug.LogWarning("[mod.io] Cannot change during transition");
            return;
        }

        int targetPageIndex = explorerView.CurrentPageNumber - 1 + direction;
        int targetPageProfileOffset = targetPageIndex * explorerView.ItemCount;

        Debug.Assert(targetPageIndex >= 0);
        Debug.Assert(targetPageIndex < explorerView.CurrentPageCount);

        int pageItemCount = (int)Mathf.Min(explorerView.ItemCount,
                                           explorerView.currentPage.resultTotal - targetPageProfileOffset);

        RequestPage<ModProfile> targetPage = new RequestPage<ModProfile>()
        {
            size = explorerView.ItemCount,
            items = new ModProfile[pageItemCount],
            resultOffset = targetPageProfileOffset,
            resultTotal = explorerView.currentPage.resultTotal,
        };
        explorerView.targetPage = targetPage;
        explorerView.UpdateTargetPageDisplay();

        RequestExplorerPage(targetPageIndex,
                            (page) =>
                            {
                                if(explorerView.targetPage == targetPage)
                                {
                                    explorerView.targetPage = page;
                                    explorerView.UpdateTargetPageDisplay();
                                }
                                if(explorerView.currentPage == targetPage)
                                {
                                    explorerView.currentPage = page;
                                    explorerView.UpdateCurrentPageDisplay();
                                    UpdateExplorerViewPageButtonInteractibility();
                                }
                            },
                            null);

        PageTransitionDirection transitionDirection = (direction < 0
                                                       ? PageTransitionDirection.FromLeft
                                                       : PageTransitionDirection.FromRight);

        explorerView.InitiateTargetPageTransition(transitionDirection, () =>
        {
            UpdateExplorerViewPageButtonInteractibility();
        });
        UpdateExplorerViewPageButtonInteractibility();
    }

    public void UpdateExplorerViewPageButtonInteractibility()
    {
        if(prevPageButton != null)
        {
            prevPageButton.interactable = (!explorerView.isTransitioning
                                           && explorerView.CurrentPageNumber > 1);
        }
        if(nextPageButton != null)
        {
            nextPageButton.interactable = (!explorerView.isTransitioning
                                           && explorerView.CurrentPageNumber < explorerView.CurrentPageCount);
        }
    }

    public void ChangeInspectorPage(int direction)
    {
        int firstExplorerIndex = (explorerView.CurrentPageNumber-1) * explorerView.ItemCount;
        int newModIndex = inspectorData.currentModIndex + direction;
        int offsetIndex = newModIndex - firstExplorerIndex;

        Debug.Assert(newModIndex >= 0);
        Debug.Assert(newModIndex <= inspectorData.lastModIndex);

        if(offsetIndex < 0)
        {
            ChangeExplorerPage(-1);

            offsetIndex += explorerView.ItemCount;
            inspectorView.profile = explorerView.targetPage.items[offsetIndex];
        }
        else if(offsetIndex >= explorerView.ItemCount)
        {
            ChangeExplorerPage(1);

            offsetIndex -= explorerView.ItemCount;
            inspectorView.profile = explorerView.targetPage.items[offsetIndex];
        }
        else
        {
            inspectorView.profile = explorerView.currentPage.items[offsetIndex];
        }

        inspectorView.statistics = null;
        inspectorView.UpdateProfileUIComponents();

        Text buttonText = inspectorView.subscribeButton.GetComponent<Text>();
        if(buttonText == null)
        {
            buttonText = inspectorView.subscribeButton.GetComponentInChildren<Text>();
        }

        if(buttonText != null)
        {
            if(subscribedModIds.Contains(inspectorView.profile.id))
            {
                buttonText.text = "View In Collection";
            }
            else
            {
                buttonText.text = "Add To Collection";
            }
        }

        ModManager.GetModStatistics(inspectorView.profile.id,
                                    (s) => { inspectorView.statistics = s; inspectorView.UpdateStatisticsUIComponents(); },
                                    null);

        inspectorData.currentModIndex = newModIndex;
        inspectorView.gameObject.SetActive(true);

        if(inspectorView.scrollView != null) { inspectorView.scrollView.verticalNormalizedPosition = 1f; }

        UpdateInspectorViewPageButtonInteractibility();
    }

    public void UpdateInspectorViewPageButtonInteractibility()
    {
        if(inspectorView.previousModButton != null)
        {
            inspectorView.previousModButton.interactable = (inspectorData.currentModIndex > 0);
        }
        if(inspectorView.nextModButton != null)
        {
            inspectorView.nextModButton.interactable = (inspectorData.currentModIndex < inspectorData.lastModIndex);
        }
    }

    // TODO(@jackson): Don't request page!!!!!!!
    public void UpdateFilters()
    {
        // sort
        if(sortByDropdown == null)
        {
            explorerViewFilter.sortFieldName = ModIO.API.GetAllModsFilterFields.popular;
            explorerViewFilter.isSortAscending = false;
        }
        else
        {
            SortOptionData optionData = sortOptions[sortByDropdown.value];
            explorerViewFilter.sortFieldName = optionData.fieldName;
            explorerViewFilter.isSortAscending = optionData.isSortAscending;
        }

        // title
        if(String.IsNullOrEmpty(nameSearchField.text))
        {
            explorerViewFilter.fieldFilters.Remove(ModIO.API.GetAllModsFilterFields.name);
        }
        else
        {
            explorerViewFilter.fieldFilters[ModIO.API.GetAllModsFilterFields.name]
                = new StringLikeFilter() { likeValue = "*"+nameSearchField.text+"*" };
        }

        // tags
        string[] filterTagNames = tagFilterView.selectedTags.ToArray();

        if(filterTagNames.Length == 0)
        {
            explorerViewFilter.fieldFilters.Remove(ModIO.API.GetAllModsFilterFields.tags);
        }
        else
        {
            explorerViewFilter.fieldFilters[ModIO.API.GetAllModsFilterFields.tags]
                = new MatchesArrayFilter<string>() { filterArray = filterTagNames };
        }

        // TODO(@jackson): BAD ZERO?
        RequestPage<ModProfile> filteredPage = new RequestPage<ModProfile>()
        {
            size = explorerView.ItemCount,
            items = new ModProfile[explorerView.ItemCount],
            resultOffset = 0,
            resultTotal = 0,
        };
        explorerView.currentPage = filteredPage;

        RequestExplorerPage(0,
                            (page) =>
                            {
                                if(explorerView.currentPage == filteredPage)
                                {
                                    explorerView.currentPage = page;
                                    explorerView.UpdateCurrentPageDisplay();
                                    UpdateExplorerViewPageButtonInteractibility();
                                }
                            },
                            null);

        // TODO(@jackson): Update Mod Count
        explorerView.UpdateCurrentPageDisplay();
    }

    public void OnSubscriptionsUpdated()
    {
        // - update explorer -
        explorerView.subscribedModIds = this.subscribedModIds;
        explorerView.UpdateCurrentPageDisplay();
        explorerView.UpdateTargetPageDisplay();

        // - update subscriptionsView -
        RequestPage<ModProfile> modPage = subscriptionsView.currentPage;
        RequestSubscriptionsPage(modPage.resultOffset,
                                 (page) =>
                                 {
                                    if(subscriptionsView.currentPage == modPage)
                                    {
                                        subscriptionsView.currentPage = page;
                                        subscriptionsView.UpdateCurrentPageDisplay();
                                    }
                                },
                                null);
    }

    public void ToggleModEnabled(ModProfile profile)
    {
        throw new System.NotImplementedException("[mod.io] This function handle is a placeholder "
                                                 + "for the enable/disable functionality that the "
                                                 + "game code may need to execute.");
    }


    // ---------[ EVENT HANDLING ]---------
    // private void OnModsAvailable(IEnumerable<ModProfile> addedProfiles)
    // {
    //     List<ModProfile> undisplayedProfiles = new List<ModProfile>(addedProfiles);
    //     List<int> cachedIds = modProfileCache.ConvertAll<int>(p => p.id);

    //     undisplayedProfiles.RemoveAll(p => cachedIds.Contains(p.id));

    //     this.modProfileCache.AddRange(undisplayedProfiles);

    //     LoadModPage();
    // }
    // private void OnModsEdited(IEnumerable<ModProfile> editedProfiles)
    // {
    //     List<ModProfile> editedProfileList = new List<ModProfile>(editedProfiles);
    //     List<int> editedIds = editedProfileList.ConvertAll<int>(p => p.id);

    //     this.modProfileCache.RemoveAll(p => editedIds.Contains(p.id));
    //     this.modProfileCache.AddRange(editedProfileList);

    //     LoadModPage();
    // }
    // private void OnModReleasesUpdated(IEnumerable<ModfileStub> modfiles)
    // {
    //     foreach(ModfileStub modfile in modfiles)
    //     {
    //         Debug.Log("Modfile Updated: " + modfile.version);
    //     }
    // }
    // private void OnModsUnavailable(IEnumerable<int> modIds)
    // {
    //     List<int> removedModIds = new List<int>(modIds);
    //     this.modProfileCache.RemoveAll(p => removedModIds.Contains(p.id));

    //     LoadModPage();
    // }
    // private void OnModsDeleted(IEnumerable<int> modIds)
    // {
    //     List<int> removedModIds = new List<int>(modIds);
    //     this.modProfileCache.RemoveAll(p => removedModIds.Contains(p.id));

    //     LoadModPage();
    // }

    // // ---------[ UI FUNCTIONALITY ]---------
    // protected virtual void LoadModPage()
    // {
    //     int pageSize = Mathf.Min(thumbnailContainer.modThumbnails.Length, this.modProfileCache.Count);
    //     this.thumbnailContainer.modIds = new int[pageSize];

    //     int i;

    //     for(i = 0; i < pageSize; ++i)
    //     {
    //         int thumbnailIndex = i;
    //         this.thumbnailContainer.modIds[i] = this.modProfileCache[i].id;
    //         this.thumbnailContainer.modThumbnails[i].sprite = CreateSpriteFromTexture(loadingPlaceholder);
    //         this.thumbnailContainer.modThumbnails[i].gameObject.SetActive(true);

    //         ModManager.GetModLogo(this.modProfileCache[i], logoThumbnailVersion,
    //                               (t) => this.thumbnailContainer.modThumbnails[thumbnailIndex].sprite = CreateSpriteFromTexture(t),
    //                               null);
    //     }

    //     while(i < this.thumbnailContainer.modThumbnails.Length)
    //     {
    //         this.thumbnailContainer.modThumbnails[i].gameObject.SetActive(false);
    //         ++i;
    //     }
    // }

    // protected virtual void OnThumbClicked(int index)
    // {
    //     ModManager.GetModProfile(thumbnailContainer.modIds[index], OnGetInspectedProfile, null);
    // }

    // protected virtual void OnGetInspectedProfile(ModProfile profile)
    // {
    //     // - set up inspector ui -
    //     inspectedProfile = profile;

    //     inspectorView.title.text = profile.name;
    //     inspectorView.author.text = profile.submittedBy.username;
    //     inspectorView.logo.sprite = CreateSpriteFromTexture(loadingPlaceholder);

    //     List<int> userSubscriptions = CacheClient.LoadAuthenticatedUserSubscriptions();

    //     if(userSubscriptions != null
    //        && userSubscriptions.Contains(profile.id))
    //     {
    //         inspectorView.subscribeButtonText.text = "Unsubscribe";
    //     }
    //     else
    //     {
    //         inspectorView.subscribeButtonText.text = "Subscribe";
    //     }

    //     ModManager.GetModLogo(profile, logoInspectorVersion,
    //                           (t) => inspectorView.logo.sprite = CreateSpriteFromTexture(t),
    //                           null);

    //     inspectorView.installButton.gameObject.SetActive(false);
    //     inspectorView.downloadButtonText.text = "Verifying local data";
    //     inspectorView.downloadButton.gameObject.SetActive(true);
    //     inspectorView.downloadButton.interactable = false;

    //     // - check binary status -
    //     ModManager.GetDownloadedBinaryStatus(profile.activeBuild,
    //                                          (status) =>
    //                                          {
    //                                             if(status == ModBinaryStatus.CompleteAndVerified)
    //                                             {
    //                                                 inspectorView.downloadButton.gameObject.SetActive(false);
    //                                                 inspectorView.installButton.gameObject.SetActive(true);
    //                                             }
    //                                             else
    //                                             {
    //                                                 inspectorView.downloadButtonText.text = "Download";
    //                                                 inspectorView.downloadButton.interactable = true;
    //                                             }
    //                                          });

    //     // - finalize -
    //     isInspecting = true;
    //     thumbnailContainer.gameObject.SetActive(false);
    //     inspectorView.gameObject.SetActive(true);
    // }

    // protected virtual void OnBackClicked()
    // {
    //     if(isInspecting)
    //     {
    //         isInspecting = false;
    //         thumbnailContainer.gameObject.SetActive(true);
    //         inspectorView.gameObject.SetActive(false);
    //     }
    // }

    // protected virtual void OnSubscribeClicked()
    // {
    //     List<int> subscriptions = CacheClient.LoadAuthenticatedUserSubscriptions();

    //     if(subscriptions == null)
    //     {
    //         subscriptions = new List<int>(1);
    //     }

    //     int modId = inspectedProfile.id;
    //     int subscriptionIndex = subscriptions.IndexOf(modId);

    //     if(subscriptionIndex == -1)
    //     {
    //         subscriptions.Add(modId);
    //         inspectorView.subscribeButtonText.text = "Unsubscribe";

    //         if(userProfile != null)
    //         {
    //             APIClient.SubscribeToMod(inspectedProfile.id,
    //                                      null, null);
    //         }
    //     }
    //     else
    //     {
    //         subscriptions.RemoveAt(subscriptionIndex);
    //         inspectorView.subscribeButtonText.text = "Subscribe";

    //         if(userProfile != null)
    //         {
    //             APIClient.UnsubscribeFromMod(inspectedProfile.id,
    //                                          null, null);
    //         }
    //     }

    //     CacheClient.SaveAuthenticatedUserSubscriptions(subscriptions);

    //     OnDownloadClicked();
    // }

    // protected virtual void OnDownloadClicked()
    // {
    //     this.activeDownload = ModManager.GetActiveModBinary(inspectedProfile);

    //     if(this.activeDownload.isDone)
    //     {
    //         inspectorView.installButton.gameObject.SetActive(true);
    //         inspectorView.downloadButton.gameObject.SetActive(true);

    //         this.activeDownload = null;
    //     }
    //     else
    //     {
    //         inspectorView.downloadButtonText.text = "Initializing Download...";

    //         this.activeDownload.succeeded += (d) =>
    //         {
    //             inspectorView.installButton.gameObject.SetActive(true);
    //             inspectorView.downloadButton.gameObject.SetActive(true);

    //             this.activeDownload = null;
    //         };
    //         this.activeDownload.failed += (d) =>
    //         {
    //             inspectorView.installButton.gameObject.SetActive(true);
    //             inspectorView.downloadButton.gameObject.SetActive(true);

    //             this.activeDownload = null;
    //         };
    //     }
    // }

    // protected virtual void OnInstallClicked()
    // {
    //     ModProfile modProfile = this.inspectedProfile;
    //     string unzipLocation = null;

    //     if(String.IsNullOrEmpty(unzipLocation))
    //     {
    //         Debug.LogWarning("[mod.io] This is a placeholder for game specific code that handles the"
    //                          + " installing the mod");
    //     }
    //     else
    //     {
    //         ModManager.UnzipModBinaryToLocation(modProfile.activeBuild, unzipLocation);
    //         // Do install code
    //     }
    // }

    // ---------[ UTILITY ]---------
    public static string ValueToDisplayString(int value)
    {
        if(value < 1000) // 0 - 999
        {
            return value.ToString();
        }
        else if(value < 100000) // 1.0K - 99.9K
        {
            // remove tens
            float truncatedValue = (value / 100) / 10f;
            return(truncatedValue.ToString() + "K");
        }
        else if(value < 10000000) // 100K - 999K
        {
            // remove hundreds
            int truncatedValue = (value / 1000);
            return(truncatedValue.ToString() + "K");
        }
        else if(value < 1000000000) // 1.0M - 99.9M
        {
            // remove tens of thousands
            float truncatedValue = (value / 100000) / 10f;
            return(truncatedValue.ToString() + "M");
        }
        else // 100M+
        {
            // remove hundreds of thousands
            int truncatedValue = (value / 1000000);
            return(truncatedValue.ToString() + "M");
        }
    }

    public static string ByteCountToDisplayString(Int64 value)
    {
        string[] sizeSuffixes = new string[]{"B", "KB", "MB", "GB"};
        int sizeIndex = 0;
        Int64 adjustedSize = value;
        while(adjustedSize > 0x0400
              && (sizeIndex+1) < sizeSuffixes.Length)
        {
            adjustedSize /= 0x0400;
            ++sizeIndex;
        }

        if(sizeIndex > 0
           && adjustedSize < 100)
        {
            decimal displayValue = (decimal)value / (decimal)(0x0400^sizeIndex);
            return displayValue.ToString("0.0") + sizeSuffixes[sizeIndex];
        }
        else
        {
            return adjustedSize + sizeSuffixes[sizeIndex];
        }
    }

    public static Sprite CreateSpriteWithTexture(Texture2D texture)
    {
        return Sprite.Create(texture,
                             new Rect(0.0f, 0.0f, texture.width, texture.height),
                             Vector2.zero);
    }
}
