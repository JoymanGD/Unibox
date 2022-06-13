using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using Dropbox.Api;
using Dropbox.Api.Files;
using System.Collections.Generic;
using UnityEngine;
using static Dropbox.Api.Common.PathRoot;
using UnityEditor;
using System.Text;

namespace Unibox
{
    public class UniboxSession
    {        
        public bool IsAlive => IsAliveInternal();
        public Action<Exception> OnErrorHandled;
        public Action<string> OnSuccessHandled;
        public Action<string> OnProcessStarted;
        public Action<string> OnProcessEnded;

        private const string RedirectUriPostfix = "authorize";
        private const string JSRedirectUriPostfix = "token";
        private const string redirectHTMLCode = "<html><script type='text/javascript'>function redirect() {document.location.href = '/token?url_with_fragment=' + encodeURIComponent(document.location.href);}</script><body onload='redirect()'/></html>";
        private DropboxClient client;
        private HttpListener http;
        private NamespaceId teamNamespaceId;

        public async Task LoginByOAuthAsync(string appKey, string redirectUriHostPart)
        {
            OnProcessStarted?.Invoke("login");

            string token = await GetAccessTokenAsync(appKey, redirectUriHostPart);

            if(token != "")
            {
                LoginByToken(token);
            }

            OnProcessEnded?.Invoke("login");
        }

        private bool IsAliveInternal()
        {
            return client != null;
        }
        
        public async Task<IList<Metadata>> ListFolderAsync(string path, bool recursive = false)
        {
            OnProcessStarted?.Invoke("list_folder");

            List<Metadata> files = new List<Metadata>();
            
            var list = await client.WithPathRoot(teamNamespaceId).Files.ListFolderAsync(path, recursive);
            files.AddRange(list.Entries);

            while(list.HasMore)
            {
                list = await client.WithPathRoot(teamNamespaceId).Files.ListFolderContinueAsync(new ListFolderContinueArg(list.Cursor));
                files.AddRange(list.Entries);
            }

            OnProcessEnded?.Invoke("list_folder");

            return files;
        }

        public async Task DownloadSingleFileAsync(string path, FileMetadata file, string savePath, bool includeProjectPath = true)
        {
            await DownloadSingleFileAsync(path+"/"+file.Name, savePath, includeProjectPath);
        }

        public async Task DownloadSingleFileAsync(string path, string fileName, string savePath, bool includeProjectPath = true)
        {
            await DownloadSingleFileAsync(path+"/"+fileName, savePath, includeProjectPath);
        }

        public async Task DownloadSingleFileAsync(string path, string savePath, bool includeProjectPath = true)
        {
            OnProcessStarted?.Invoke("download_single");

            try
            {
                var finalSavePath = savePath;
                
                if(includeProjectPath)
                {
                    var separator = finalSavePath.StartsWith("/") ? "" : "/";
                    var projectPath = Application.dataPath.Replace("/Assets", "");
                    finalSavePath = projectPath + separator + finalSavePath;
                }

                using (var response = await client.WithPathRoot(teamNamespaceId).Files.DownloadAsync(path))
                {
                    using (FileStream fileStream = File.Create(finalSavePath))
                    {
                        (await response.GetContentAsStreamAsync()).CopyTo(fileStream);
                        
                        OnSuccessHandled("Downloaded successfully!");
                        #if UNITY_EDITOR
                        AssetDatabase.Refresh();
                        #endif
                    }
                }
            }
            catch(Exception e)
            {
                OnErrorHandled?.Invoke(e);
            }

            OnProcessEnded?.Invoke("download_single");
        }

        public async Task DownloadFilesFromFolderAsync(string path, string savePath, bool includeProjectPath = true, bool recursive = false)
        {
            OnProcessStarted?.Invoke("download_all");

            try
            {
                var finalSavePath = savePath;
                finalSavePath = finalSavePath + (finalSavePath.EndsWith("/") ? "" : "/");
                
                if(includeProjectPath)
                {
                    var separator = finalSavePath.StartsWith("/") ? "" : "/";
                    var projectPath = Application.dataPath.Replace("/Assets", "");
                    finalSavePath = projectPath + separator + finalSavePath;
                }

                var contentList = await ListFolderAsync(path, recursive);

                foreach (var dropboxFileInfo in contentList) 
                {
                    if(dropboxFileInfo.IsFile)
                    {
                        using (var response = await client.WithPathRoot(teamNamespaceId).Files.DownloadAsync(dropboxFileInfo.PathLower.ToString()))
                        {
                            using (FileStream fileStream = File.Create(finalSavePath + dropboxFileInfo.Name))
                            {
                                (await response.GetContentAsStreamAsync()).CopyTo(fileStream);
                            }
                        }
                    }
                }

                OnSuccessHandled("Downloaded successfully!");
                #if UNITY_EDITOR
                AssetDatabase.Refresh();
                #endif
            }
            catch(Exception e)
            {
                OnErrorHandled?.Invoke(e);
            }
            
            OnProcessEnded?.Invoke("download_all");
        }

        private async void LoginByToken(string token)
        {
            var httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromMinutes(20)
            };

            try
            {
                var config = new DropboxClientConfig("SimpleTestApp")
                {
                    HttpClient = httpClient
                };

                client = new DropboxClient(token, config);
                
                var tclient = new DropboxTeamClient(token, config); 
                var adm = await tclient.Team.TokenGetAuthenticatedAdminAsync(); 
                var teamMemberId = adm.AdminProfile.TeamMemberId;

                client = tclient.AsMember(teamMemberId);

                //getting teamNamespaceId for api calls directed to team folder
                var account = await client.Users.GetCurrentAccountAsync(); 
                teamNamespaceId = new NamespaceId(account.RootInfo.RootNamespaceId);

                OnSuccessHandled?.Invoke("Login succeed!");
            }
            catch (HttpException e)
            {
                OnErrorHandled?.Invoke(e);
            }
        }
        
        private async Task<string> GetAccessTokenAsync(string apiKey, string redirectUriHostPart)
        {
            string accessToken = "";

            try
            {
                Uri redirectUri = new Uri(redirectUriHostPart + RedirectUriPostfix);
                Uri jsRedirectUri = new Uri(redirectUriHostPart + JSRedirectUriPostfix);

                var state = Guid.NewGuid().ToString("N");
                
                var authorizeUri = DropboxOAuth2Helper.GetAuthorizeUri(OAuthResponseType.Token, apiKey, redirectUri, state: state);
                
                if(http == null)
                {
                    http = new HttpListener();
                    http.Prefixes.Add(redirectUriHostPart);
                    
                    http.Stop();
                    http.Abort();
                    
                    http.Start();
                }

                Process.Start(authorizeUri.ToString());

                await HandleOAuth2Redirect(http, redirectUri); 

                var result = await HandleJSRedirect(http, jsRedirectUri);

                if (result.State != state)
                {
                    return null;
                }

                accessToken = result.AccessToken;
                var uid = result.Uid;
            }
            catch (Exception e)
            {
                OnErrorHandled(e);
            }

            return accessToken;
        }
        
        private async Task HandleOAuth2Redirect(HttpListener http, Uri redirectUri)
        {
            var context = await http.GetContextAsync();

            while (context.Request.Url.AbsolutePath != redirectUri.AbsolutePath)
            {
                context = await http.GetContextAsync();
            }

            context.Response.ContentType = "text/html";

            Byte[] info = new UTF8Encoding(true).GetBytes(redirectHTMLCode);
            context.Response.OutputStream.Write(info, 0, info.Length);

            context.Response.OutputStream.Close();
        }

        private async Task<OAuth2Response> HandleJSRedirect(HttpListener http, Uri redirectUri)
        {
            var context = await http.GetContextAsync();

            while (context.Request.Url.AbsolutePath != redirectUri.AbsolutePath)
            {
                context = await http.GetContextAsync();
            }

            var resultRedirectUri = new Uri(context.Request.QueryString["url_with_fragment"]);

            var result = DropboxOAuth2Helper.ParseTokenFragment(resultRedirectUri);

            return result;
        }
    }
} 
