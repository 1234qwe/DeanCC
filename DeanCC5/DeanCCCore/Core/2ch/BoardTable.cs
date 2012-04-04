﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using DeanCCCore.Core.Utility;
using DeanCCCore.Core._2ch.Utility;

namespace DeanCCCore.Core._2ch
{
    [Serializable]
    public sealed class BoardTable : CategoryCollection, IBoardTable
    {
        private static readonly HttpStatusCode[] ContinueStatuses = { HttpStatusCode.ServiceUnavailable, HttpStatusCode.RequestTimeout };

        [field: NonSerialized]
        public event EventHandler<BoardTableUpdateEventArgs> OnlineUpdated;

        public BoardTable()
        {
        }

        [NonSerialized]
        private bool updateCompleted;
        public bool UpdateCompleted
        {
            get { return updateCompleted; }
        }

        public void Add(IBoardTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException("table");
            }
            foreach (ICategory category in this)
            {
                this.Add(category);
            }
        }

        public bool Contains(IBoardInfo board)
        {
            if (board == null)
            {
                throw new ArgumentNullException("board");
            }
            return this.Any(category => category.Children.Contains(board));
        }

        public ICategory FindFromBoardInfo(IBoardInfo board)
        {
            return this.FirstOrDefault(category => category.Children.Contains(board));
        }

        //2ch以外も対応しているとnameが被る可能性があるのでdomainPathもチェックしている
        public IBoardInfo FindFromName(string name, string domainPath)
        {
            foreach (ICategory category in this)
            {
                IBoardInfo info = category.Children.FirstOrDefault(board => board.Name.Equals(name));
                if (info != null && info.DomainPath.Equals(domainPath))
                {
                    return info;
                }
            }
            return null;
        }
        //2chのみならこれでBoardInfoを特定可能
        public IBoardInfo FindFromPath(string path)
        {
            foreach (ICategory category in this)
            {
                IBoardInfo info = category.Children.FirstOrDefault(board => board.Path.Equals(path));
                if (info != null)
                {
                    return info;
                }
            }
            return null;
        }

        public IBoardInfo FindFromUrl(string url)
        {
            foreach (Category category in this)
            {
                IBoardInfo info = category.Children.FirstOrDefault(board => board.Url.Equals(url));
                if (info != null)
                {
                    return info;
                }
            }
            return null;
        }

        /// <summary>
        /// 板移転の追尾
        /// </summary>
        /// <remarks>
        /// 板一覧取得先URLから現在有効な板URLを取得して適用する(Subject.Txtの中を見て移転先を決めていない）
        /// </remarks>
        public void FollowMovedBoard()
        {
            OnlineUpdate();
            AppliePatrolPatternBoards();
            Common.Logs.Add("板移転追尾", "", LogStatus.System);
        }

        private void AppliePatrolPatternBoards()
        {
            foreach (GenreFolder genre in Common.PatrolPatterns)
            {
                foreach (PatrolPattern pattern in genre)
                {
                    foreach (BoardInfo oldBoard in pattern.TargetBoards)
                    {
                        IBoardInfo newBoard = FindFromPath(oldBoard.Path);
                        if (newBoard != null && oldBoard.Server != newBoard.Server)
                        {
                            oldBoard.ChangeBoard(newBoard);
                        }
                        //else
                        //{
                        //    FindCurrentServer(oldBoard);
                        //}
                    }
                }
            }
        }

        //private string FindCurrentServer(IBoardInfo oldBoard)
        //{
        //    InternetClient.DownloadResult result = InternetClient.DownloadData(oldBoard.SubjectUrl);
        //    if (result.Success && !string.IsNullOrEmpty(result.Data))
        //    {
        //        Regex matchNewURL = new Regex(@"<a href=""(?<url>.*?)"".*?>(?<text>.*?)</a>");
        //        Regex matchTitle = new Regex(@"<title>(?<title>.*?)</title>");
        //        if (matchTitle.Match(result.Data).Groups["title"].Value == "2chbbs..")//板移転
        //        {
        //            string newurl = matchNewURL.Match(result.Data).Groups["url"].Value;
        //            IBoardInfo board = FindFromUrl(url);
        //            if (board != null)
        //            {
        //                Replace(board, ThreadUtility.ParseBoardInfo(newurl, board.Name));

        //                return newurl;
        //            }
        //        }
        //    }

        //    return url;
        //}

        /// <summary>
        /// 板一覧を現在の設定URLでアップデート
        /// </summary>
        public void OnlineUpdate()
        {
            OnlineUpdate(Common.Options.InternetOptions.BoardTableSourceUrl);
        }

        /// <summary>
        /// 板一覧を指定したURLでアップデート
        /// </summary>
        /// <param name="url"></param>
        public void OnlineUpdate(string url)
        {
            BoardTableUpdateEventArgs e = new BoardTableUpdateEventArgs();
            e.LastModified = lastModified;
            try
            {
                InternetClient.DownloadResult result = InternetClient.DownloadData(url, lastModified);
                if (!result.Success)
                {
                    OnOnlineUpdated(e);
                    return;
                }

                e.LastModified = result.LastModified;
                string str = result.Data;
                string[] strArray = Regex.Split(Regex.Replace(str, "<br><b>", "\r\n<br><b>", RegexOptions.IgnoreCase), "\r\n|\r|\n");
                Regex regex = new Regex("<br><b>(?<category>.+?)</b><br>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex regex2 = new Regex(@"<a href=(?<url>[^\s>]+).*?>(?<subj>.+?)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                int index = 0;
                CategoryCollection categories = new CategoryCollection();
                while (index < strArray.Length)
                {
                    Match match = regex.Match(strArray[index++]);
                    if (match.Success)
                    {
                        Category item = new Category(match.Groups["category"].Value);
                        while (index < strArray.Length)
                        {
                            if (strArray[index] == string.Empty)
                            {
                                break;
                            }
                            Match match2 = regex2.Match(strArray[index++]);
                            if (match2.Success)
                            {
                                BoardInfo info = ThreadUtility.ParseBoardInfo(match2.Groups["url"].Value, item.Name);
                                if (info != null)
                                {
                                    info.Name = match2.Groups["subj"].Value;
                                    item.Children.Add(info);
                                    IBoardInfo oldInfo = FindFromName(info.Name, info.DomainPath);
                                    if (oldInfo != null)
                                    {
                                        Replace(oldInfo, info);
                                    }
                                }
                            }
                        }
                        if (item.Children.Count > 0)
                        {
                            categories.Add(item);
                        }
                    }
                }

                if (categories.Count <= 0)
                {
                    throw new ApplicationException("板一覧の更新に失敗しました");
                }
                this.Clear();
                foreach (Category category in categories)
                {
                    Add(category);
                }
                e.Updated = true;
            }
            catch (System.Net.WebException ex)
            {
                if (ex.Status == WebExceptionStatus.ProtocolError)
                {
                    HttpWebResponse errorResponse = (HttpWebResponse)ex.Response;
                    if (ContinueStatuses.Contains(errorResponse.StatusCode))
                    {
                        e.Message = ex.Message;
                    }
                    else if (HttpStatusCode.NotModified != errorResponse.StatusCode)
                    {
                        throw;
                    }
                }
                else//プロトコルエラー以外の例外
                {
                    e.Message = ex.Message;
                }
            }

            OnOnlineUpdated(e);
        }

        public void OnOnlineUpdated(BoardTableUpdateEventArgs e)
        {
            lastModified = e.LastModified;
            updateCompleted = true;
            if (OnlineUpdated != null)
            {
                OnlineUpdated(this, e);
            }
        }

        public void Replace(IBoardInfo oldBoard, IBoardInfo newBoard)
        {
            if (oldBoard == null)
            {
                throw new ArgumentNullException("oldBoard");
            }
            if (newBoard == null)
            {
                throw new ArgumentNullException("newBoard");
            }
            foreach (ICategory category in this)
            {
                int index = category.Children.IndexOf(oldBoard);
                if (index != -1)
                {
                    IBoardInfo info = category.Children[index];
                    info.Path = newBoard.Path;
                    info.Server = newBoard.Server;
                }
            }
        }

        public IBoardInfo[] ToArray()
        {
            List<BoardInfo> list = new List<BoardInfo>();
            foreach (ICategory category in this)
            {
                foreach (BoardInfo info in category.Children)
                {
                    list.Add(info);
                }
            }
            return list.ToArray();
        }

        private DateTime lastModified;
        public DateTime LastModified
        {
            get { return lastModified; }
            set { lastModified = value; }
        }
    }
}
