﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using TwitchLib.Api.Helix.Models.Users.GetUsers;
using TwitchLib.Api;
using System.IO;
using EventSub;
using Newtonsoft.Json;
using Microsoft.Toolkit.Uwp.Notifications;
using TwitchLib.Api.Helix.Models.Streams.GetStreams;
using System.Diagnostics.CodeAnalysis;
using TwitchLib.Api.Helix.Models.Users.GetUserFollows;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Helix.Models.Games;
using System.Windows.Documents;
using System.Threading;
using Stream = TwitchLib.Api.Helix.Models.Streams.GetStreams.Stream;
using TwitchLib.Api.Helix.Models.Streams.GetFollowedStreams;
using TwitchLib.Api.Helix;
using System.Collections.ObjectModel;

namespace TwatApp.Models
{
    /// <summary>
    /// class responsible for notifying a user, using windows toast notifications, based on registered streamers and their category conditions.
    /// 
    /// use addStreamers and removeStreamrs to register or unregister streamers for notifications.
    /// streamersFromIds or streamersFromNames can be used for retrieving IStreamer instances, from various broadcaster identifiers.
    /// 
    /// same for categoriesFromIds and categoriesFromNames.
    /// 
    /// use startNotify and stopNotify to control when an instance will send toast notifications.
    /// 
    /// </summary>
    public class TwitchNotify
    {
        #region public

        /// <summary>
        /// how often the twitch api will be polled in seconds.
        /// if the value is too low, rate limit exceptions may occur.
        /// </summary>
        public int PollInterval { get; set; } = 60;
        /// <summary>
        /// invoked whenever any of the registered streamers change their stream status or category.
        /// </summary>
        public EventHandler<IStreamerInfo>? StreamerChanged;
        /// <summary>
        /// readonly dictionary of the registered streamers, mapping the streamer id to the corresponding IStreamerInfo instance
        /// </summary>
        public ReadOnlyDictionary<string, IStreamerInfo> Streamers { get => new(m_streamers); }

        /// <summary>
        /// construct TwitchNotify instance with the passed twitch client id.
        /// this id will be used when making calls to the twitch api.
        /// </summary>
        public TwitchNotify(string client_id)
        {
            m_twitch_api.Settings.ClientId = client_id;
        }

        /// <summary>
        /// start the poll thread, and activate notifying of streamers going live.
        /// 
        /// must be called after user has been authorized.
        /// 
        /// </summary>
        public void startNotify()
        {
            m_polling = true;

            m_poll_thread = new Thread(async () => await pollThread());
            m_poll_thread.Start();
        }

        /// <summary>
        /// stop notifying when a streamer goes live.
        /// </summary>

        public void stopNotify()
        {
            m_polling = false;

            m_poll_thread?.Join();
            m_poll_thread = null;
        }

        /// <summary>
        /// authenticate current user using an implicit OAuth2 flow.
        /// optionally store the retrieved token in a file, in order to avoid opening up a tab in the default browser, every time the program is run
        /// </summary>
        /// <param name="token_file_dir"> specify the name of the file containing a token. If null, no token file will be used. </param>
        /// <param name="force_verify"> specfifies whether to force the user to reverify themselves, even if they have already been verified before. </param>
        /// <returns></returns>
        public async Task<bool> authUser(string? token_file_dir, bool force_verify)
        {
            string token = "";

            if (File.Exists(token_file_dir))
                token = File.ReadAllText(token_file_dir);

            // TODO: expand check if token is valid

            if (token.Length < 10)
            {
                HttpClient client = new();

                UriBuilder auth_endpoint = new("https://id.twitch.tv/oauth2/authorize");

                var query = HttpUtility.ParseQueryString("");

                query["client_id"] = m_twitch_api.Settings.ClientId;
                query["scope"] = "user:read:follows channel:moderate";
                query["response_type"] = "token";
                query["redirect_uri"] = "http://localhost:3000/";
                query["force_verify"] = force_verify.ToString();

                auth_endpoint.Query = query.ToString();

                Console.WriteLine(auth_endpoint.Uri);
                HttpRequestMessage msg = new(HttpMethod.Get, auth_endpoint.Uri);

                HttpResponseMessage rmsg = await client.SendAsync(msg);
                Trace.WriteLine(await rmsg.Content.ReadAsStringAsync());

                Process.Start(new ProcessStartInfo() { FileName = auth_endpoint.Uri.ToString(), UseShellExecute = true });

                HttpListener listener = new();
                listener.Prefixes.Add(query["redirect_uri"]);
                listener.Start();

                while (true)
                {
                    var context = listener.GetContext();

                    var req = context.Request;
                    var res = context.Response;
                    res.ContentType = "text/html";

                    if (req.QueryString["error"] != null)
                    {
                        return false;
                    }
                    else if (req.QueryString.Count == 0)
                    {
                        var resp = File.ReadAllBytes("parse_token.html");
                        res.OutputStream.Write(resp);

                        res.Close();
                    }
                    else
                    {
                        token = req.QueryString["access_token"];
                        var resp = File.ReadAllBytes("close_tab.html");
                        res.OutputStream.Write(resp);

                        res.Close();
                        break;
                    }
                }

                if(token_file_dir != null)
                    File.WriteAllText(token_file_dir, token);
            }

            m_twitch_api.Settings.AccessToken = token;

            // update user id

            GetUsersResponse current_user = await m_twitch_api.Helix.Users.GetUsersAsync();

            m_user_id = current_user.Users[0].Id;

            return true;
        }

        /// <summary>
        /// get a list of streamers from the specified streamer names.
        /// </summary>
        /// <param name="names"> list of names to convert to IStreamer instances </param>
        public async Task<List<IStreamer>> streamersFromNames(List<string> names)
        {
            if (names.Count == 0)
                return new();

            GetUsersResponse users = await m_twitch_api.Helix.Users.GetUsersAsync(logins: names);

            return streamersFromResponse(users);
        }

        /// <summary>
        /// get a list of streamers form the specified streamer ids
        /// </summary>
        public async Task<List<IStreamer>> streamersFromIds(List<string> ids)
        {
            // if an ids list of length 0 is passed to GetUsersAsync, it will assume no ids have been passed,
            // and then return 20 twitch users based on some random property, which is not what we want,
            // so we check if the ids count is 0 here, in order to avoid this problem.
            if (ids.Count == 0)
                return new();

            GetUsersResponse users = await m_twitch_api.Helix.Users.GetUsersAsync(ids: ids );

            return streamersFromResponse(users);
        }

        /// <summary>
        /// get a list of all the streamers the verified user is currently following.
        /// </summary>
        public async Task<List<IStreamer>> followedStreamers()
        {
            GetUsersFollowsResponse response = await m_twitch_api.Helix.Users.GetUsersFollowsAsync(fromId: m_user_id, first: 100);

            List<string> ids = new((int)response.TotalFollows);

            foreach (Follow follow in response.Follows)
                ids.Add(follow.ToUserId);

            return await streamersFromIds(ids);
        }
        
        /// <summary>
        /// get category instances from category names.
        /// 
        /// makes use of the same cache strategy as the categoriesFromIds method.
        /// is way less efficient, as the name needs to be searches for in the cache using a linear method,
        /// so please use the categoriesFromIds if the category id is known.
        /// 
        /// </summary>
        public async Task<List<ICategory>> categoriesFromNames(List<string> names)
        {
            List<string> non_cached_names = names.Where(name => m_cached_categories.Values.Any(value => value.Name == name)).ToList();

            if (non_cached_names.Count > 0)
            {
                GetGamesResponse categories = await m_twitch_api.Helix.Games.GetGamesAsync(gameNames: non_cached_names);

                List<ICategory> new_categories = categoriesFromResponse(categories);

                // add new categories to the cache

                foreach (ICategory new_category in new_categories)
                    m_cached_categories[new_category.Id] = new_category;
            }

            // now act as if all ids exist in the cache

            return m_cached_categories.Values.Where(category => names.Contains(category.Name)).ToList();
        }

        /// <summary>
        /// get category instances from category ids.
        /// 
        /// additionally, if the category has already been retrieved,
        /// this method returns a cached ICategory instance, instead of performing a get request to the twitch api-
        /// </summary>
        public async Task<List<ICategory>> categoriesFromIds(List<string> ids)
        {

            List<string> non_cached_ids = ids.Where(id => !m_cached_categories.ContainsKey(id)).ToList();

            if (non_cached_ids.Count > 0)
            {
                GetGamesResponse categories = await m_twitch_api.Helix.Games.GetGamesAsync(gameIds: ids);

                List<ICategory> new_categories = categoriesFromResponse(categories);

                // add new categories to the cache

                foreach (ICategory new_category in new_categories)
                    m_cached_categories[new_category.Id] = new_category;
            }

            // now act as if all ids exist in the cache

            return m_cached_categories.Values.Where(category => ids.Contains(category.Id)).ToList();
        }

        /// <summary>
        /// add the passed streamers to the streamer list and associate a default IStreamerInfo instance to the streamer.
        /// if the streamer already exists in the list, nothing is done.
        /// </summary>
        public async Task addStreamers(List<IStreamer> streamers)
        {
            foreach (IStreamer streamer in streamers)
                if (!m_streamers.ContainsKey(streamer.Id))
                    m_streamers[streamer.Id] = new StreamerInfo(streamer);

            await streamerIcons(streamers);
        }

        /// <summary>
        /// remove the streamer from the streamer list.
        /// 
        /// this means the IStreamInfo instance is also forgotten, so if the streamer is later readded, the categories and so on will not be remembered.
        /// 
        /// if the intention is to temporarily stop notifications from the passed streamers,
        /// please use the associated IStreamerInfos Disable property isntead.
        /// </summary>
        public void removeStreamers(List<IStreamer> streamers)
        {
            foreach (IStreamer streamer in streamers)
            {
                m_streamers.Remove(streamer.Id);
                File.Delete(streamer.IconFile);
            }
        }

        /// <summary>
        /// save the currently registered streamers, and their correspoinding info, to a specified json file, that can later be loaded.
        /// </summary>
        /// <param name="file"></param>
        public void saveConfiguration(string file)
        {
            File.WriteAllText(file, JsonConvert.SerializeObject(m_streamers, Formatting.Indented));
        }

        /// <summary>
        /// loads a previously saved configuration from the passed json file.
        /// overwrites any currently registered streamers.
        /// </summary>
        /// <param name="file"></param>
        public void loadConfiguration(string file)
        {
            if(File.Exists(file))
            {
                string contents = File.ReadAllText(file);

                m_streamers = JsonConvert.DeserializeObject<Dictionary<string, IStreamerInfo>>(contents)!;
            }

        }

        /// <summary>
        /// retrieve a list of all the currently registered streamers info instances.
        /// </summary>
        public List<IStreamerInfo> currentStreamers()
        {
            return m_streamers.Values.ToList();
        }

        #endregion

        #region protected fields

        protected TwitchAPI m_twitch_api = new();
        protected string? m_user_id;
        protected HttpClient m_http_client = new();
        protected Dictionary<string, IStreamerInfo> m_streamers = new();
        protected bool m_polling = false;
        protected Thread? m_poll_thread = null;
        protected Dictionary<string, ICategory> m_cached_categories = new();

        #endregion

        #region protected methods

        // construct a list of IStreamer objecst, based on the Users property in the passed GetUserResponse instance.
        protected List<IStreamer> streamersFromResponse(GetUsersResponse users)
        {
            List<IStreamer> streamers = new(users.Users.Length);

            foreach(User user in users.Users)
                streamers.Add(new Streamer(user.Id, user.DisplayName, user.ProfileImageUrl));

            return streamers;
        }

        // construct a list of ICategory objects, based on the Game property in the passed GetGamesResponse instance.
        protected List<ICategory> categoriesFromResponse(GetGamesResponse games)
        {
            List<ICategory> categories = new(games.Games.Length);

            foreach (Game game in games.Games)
                categories.Add(new Category(game.Id, game.Name));

            return categories;
        }
        protected async Task streamerIcons(List<IStreamer> streamers)
        {
            foreach (IStreamer streamer in streamers)
            {
                string full_icon_path = Path.GetFullPath(streamer.IconFile);

                if (File.Exists(full_icon_path))
                    continue;

                Directory.CreateDirectory(Directory.GetParent(full_icon_path)!.ToString());
                await File.WriteAllBytesAsync(full_icon_path, await m_http_client.GetByteArrayAsync(streamer.IconUri));
            }
        }

        // thread responsible for polling the twitch api
        protected async Task pollThread()
        {
            while(m_polling)
            {
                List<string> ids = new(m_streamers.Count);

                // if there are no registered streamers, simply do nothing and wait for the poll interval,
                // before checking if any streamers have been registered.

                if (m_streamers.Count == 0)
                {
                    Thread.Sleep(PollInterval * 1000);
                    continue;
                }

                foreach(IStreamerInfo streamer_info in m_streamers.Values)
                {
                    if (!streamer_info.Disable)
                        ids.Add(streamer_info.Streamer.Id);
                }

                GetStreamsResponse response = await m_twitch_api.Helix.Streams.GetStreamsAsync(userIds: ids);

                HashSet<string> live_users = new();

                // handle users that went live

                foreach(Stream stream in response.Streams)
                {
                    IStreamerInfo streamer_info = m_streamers[stream.UserId];

                    live_users.Add(stream.UserId);

                    await updateStreamInfo(streamer_info, stream.Type == "live", stream.GameId);
                }

                // handle users that are not / no longer live

                foreach(IStreamerInfo streamer_info in m_streamers.Values.Where(info => !live_users.Contains(info.Streamer.Id)))
                    await updateStreamInfo(streamer_info, false, null);

                Thread.Sleep(PollInterval * 1000);
            }
        }

        // update the passed streamer_info api and call StreamerChanged and streamerNotify if necessary
        protected async Task updateStreamInfo(IStreamerInfo streamer_info, bool is_live, string? category_id)
        {
            bool state_changed = false;

            // check if a toast noficiation should be sent
            // this should happen if the user has just gone live, and the filtered categories are correct.
            // 
            // the first time this is called, the IsLive property will be null.
            // currently the user will not get a notification, when a streamer is polled for the first time.
            if(!(streamer_info.IsLive ?? true) && is_live)
            {
                state_changed = true;

                bool should_notify;

                if (streamer_info.FilteredCategories.Count == 0)
                {
                    should_notify = true;
                }
                else
                {
                     should_notify = !streamer_info.WhitelistCategories;

                    foreach (ICategoryInfo category_info in streamer_info.FilteredCategories)
                    {
                        if (category_info.Category.Id == category_id)
                        {
                            if (!category_info.Disable)
                                should_notify = !should_notify;

                            break;
                        }
                    }
                }

                if (should_notify)
                    streamerNotify(streamer_info);
            }

            // update fields.

            (streamer_info as StreamerInfo)!.is_live = is_live;

            if (category_id != null && category_id != "")
                (streamer_info as StreamerInfo)!.current_category = (await categoriesFromIds(new() { category_id }))[0];
            else
                (streamer_info as StreamerInfo)!.current_category = null;
            
            if(state_changed)
                StreamerChanged?.Invoke(this, streamer_info);
        }

        // send toast notification containing the streamer name and icon, broadcast category, and argument values for later click handeling.
        protected void streamerNotify(IStreamerInfo streamer_info)
        {
            new ToastContentBuilder().
                AddText($"{streamer_info.Streamer.DisplayName} Just started streaming {streamer_info.CurrentCategory?.Name ?? ""}!").
                AddAppLogoOverride(new(streamer_info.Streamer.IconFile), ToastGenericAppLogoCrop.Circle).
                AddAttributionText("Click to go to stream").
                AddArgument("streamer", streamer_info.Streamer.LoginName).
                Show();
        }


        #endregion

        #region interface implementations

        class Streamer : IStreamer
        {
            public string Id => m_id;
            public string DisplayName => m_name;
            public string IconUri => m_icon_uri;

            public Streamer(string id, string name, string icon_uri)
            {
                m_id = id;
                m_name = name;
                m_icon_uri = icon_uri;
            }

            protected string m_name;
            protected string m_id;
            protected string m_icon_uri;
        }

        class StreamerInfo : IStreamerInfo
        {
            public StreamerInfo(IStreamer streamer, List<ICategoryInfo>? categories = null, bool whitelist = true, bool disable = false)
            {
                this.streamer = streamer;
                this.categories = categories ?? new();
                this.whitelist = whitelist;
                this.disable = disable;
            }

            public IStreamer Streamer => streamer;

            public List<ICategoryInfo> FilteredCategories => this.categories;
            public bool WhitelistCategories { get => this.whitelist; set => this.whitelist = value; }
            public bool Disable { get => this.disable; set => this.disable = value; }

            public ICategory? CurrentCategory => current_category;

            public bool? IsLive => is_live;

            [JsonIgnore]
            public IStreamer streamer;
            [JsonIgnore]
            public List<ICategoryInfo> categories = new();
            [JsonIgnore]
            public bool whitelist = true;
            [JsonIgnore]
            public bool disable = false;
            [JsonIgnore]
            public ICategory? current_category = null;
            [JsonIgnore]
            public bool? is_live = null;
        }

        public class Category : ICategory
        {
            public string Name => m_name;

            public string Id => m_id;


            public Category(string id, string name)
            {
                m_id = id;
                m_name = name;
            }

            protected string m_name;
            protected string m_id;
        }

        public class CategoryInfo : ICategoryInfo
        {
            public CategoryInfo(ICategory category, bool disable = false)
            {
                m_category = category;
                m_disable = disable;
            }

            public ICategory Category => m_category;

            public bool Disable { get => m_disable; set => m_disable = value; }

            protected bool m_disable;
            protected ICategory m_category;

        }

        #endregion

    }
}
