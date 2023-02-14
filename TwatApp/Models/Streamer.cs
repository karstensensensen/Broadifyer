﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace TwatApp.Models
{
    /// <summary>
    /// represents an identifier class that represents a twitch broadcaster
    /// </summary>
    public interface IStreamer : IEqualityComparer<IStreamer>
    {
        /// <summary>
        /// id of the broadcaster
        /// </summary>
        public string Id { get; }
        /// <summary>
        /// display name of the broadcaster.
        /// this will be what the user will see, when searching for the broadcaster, and when viewing their profile page.
        /// </summary>
        public string DisplayName { get; }
        /// <summary>
        /// currently the displayname without capitalization.
        /// </summary>

        [JsonIgnore]
        public string LoginName => DisplayName.ToLower();
        /// <summary>
        /// path to where the broadcaster icon / profile image should be stored.
        /// </summary>

        [JsonIgnore]
        public string IconFile => Path.GetFullPath($"icons/streamers/{Id}.png");
        /// <summary>
        /// uri to where the broadcaster icon / profile image is stored online.
        /// </summary>

        [JsonIgnore]
        public string IconUri { get; }

        bool IEqualityComparer<IStreamer>.Equals(IStreamer? x, IStreamer? y)
        {
            if (x == null || y == null)
                return false;

            return x.Id == y.Id;
        }

        int IEqualityComparer<IStreamer>.GetHashCode(IStreamer obj)
        {
            return obj.Id.GetHashCode();
        }
    }

    /// <summary>
    /// class representing various configuration and state info about a specific IStreamer instance.
    /// </summary>

    public interface IStreamerInfo
    {
        /// <summary>
        /// the IStreamer instance this class contains data about.
        /// </summary>
        public IStreamer Streamer { get; }
        /// <summary>
        /// what categories should be filtered, when deciding wheather to send a notification or not.
        /// 
        /// wheather the notification will be sent if the broadcaster is streaming or not streaming the category,
        /// is dependent on the WhitelistCategories property.
        /// 
        /// </summary>
        public List<ICategoryInfo> FilteredCategories { get; }
        /// <summary>
        /// if true:
        ///     the user will only be sent a notification, if the streamer is streaming any of the filtered categories
        /// if false:
        ///     notification is sent if streamer is NOT streaming any of the filtered categories.
        /// </summary>
        public bool WhitelistCategories { get; set; }
        /// <summary>
        /// ICategory instance that the broadcaster is currently streaming.
        /// </summary>
        [JsonIgnore]
        public ICategory? CurrentCategory { get; }
        /// <summary>
        /// wheather the broadcaster is currently live.
        /// </summary>
        [JsonIgnore]
        public bool? IsLive { get; }
        /// <summary>
        /// if true:
        ///     no notifications will be sent, no matter what
        /// if false:
        ///     notifications will be sent, if the live and filtered categories conditions are met.
        /// </summary>
        public bool Disable { get; set; }
    }
}
