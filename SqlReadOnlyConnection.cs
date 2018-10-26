//using AntaresDiagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.Cis.Security.StashBox.svc;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace SqlReadOnlyUtils
{
    public class SqlReadOnlyConnection : IDisposable
    {
        static ConcurrentDictionary<string, string> SqlConnectionStrings = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static ConcurrentDictionary<string, SqlConnection> SqlConnections = new ConcurrentDictionary<string, SqlConnection>(StringComparer.OrdinalIgnoreCase);
        //static Lazy<AntaresDiagnosticsCommon> AntaresDiagnosticsCommon = new Lazy<AntaresDiagnosticsCommon>(() =>
        //{
        //    var diagnostics = new AntaresDiagnosticsCommon();
        //    diagnostics.Initialize();
        //    return diagnostics;
        //});

        static Lazy<string> CacheDirectory = new Lazy<string>(() =>
        {
            var dir = System.Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\SqlReadOnlyUtils");
            Directory.CreateDirectory(dir);
            return dir;
        });

        static string _clientInfo;
        static AuthenticationResult _authenticationToken;
        const string BaseAddress = "https://wawshealthapi.azurewebsites.net/";
        //const string CertAuthAADClientId "243a0dd6-ea81-4afe-9614-a572c6d8c873";
        const string UserAuthAADClientId = "8bed4a37-bc4b-4ac5-9dd9-6f750ce43094";

        string _connStr;
        SqlConnection _connection;

        public SqlReadOnlyConnection(string connStr)
        {
            if (!SqlConnections.TryGetValue(connStr, out _connection))
            {
                var connection = new SqlConnection(connStr);
                connection.Open();
                SqlConnections[_connStr = connStr] = _connection = connection;
            }
        }

        public static SqlReadOnlyConnection Get(string stampName)
        {
            try
            {
                var connStr = GetSqlConnectionString(stampName);
                if (!string.IsNullOrEmpty(connStr))
                    return new SqlReadOnlyConnection(connStr);
            }
            catch (Exception ex)
            {
                if (!ex.ToString().Contains("Login failed for user") &&
                    !ex.ToString().Contains("The server was not found or was not accessible"))
                {
                    throw new InvalidOperationException(stampName + ": " + ex.Message, ex);
                }
            }

            try
            {
                return new SqlReadOnlyConnection(GetSqlConnectionString(stampName, forceRenew: true));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(stampName + ": " + ex.Message, ex);
            }
        }

        public void Open()
        {
            // connection is always open at Ctor time
        }

        public void Close()
        {
            SqlConnection unused;
            SqlConnections.TryRemove(_connStr, out unused);
            _connection.Dispose();
        }

        public void Dispose()
        {
            Close();
        }

        public static implicit operator SqlConnection(SqlReadOnlyConnection x)
        {
            return x._connection;
        }

        public static void CleanUp()
        {
            foreach (var pair in SqlConnections)
            {
                pair.Value.Close();
            }

            SqlConnections.Clear();
        }

        public static string GetSqlConnectionString(string stampName, bool forceValidate = false, bool forceRenew = false)
        {
            string connStr = null;
            if (!forceRenew && SqlConnectionStrings.TryGetValue(stampName, out connStr))
            {
                return connStr;
            }

            var file = string.Format(@"{0}\{1}.ro-sql.txt", CacheDirectory.Value, stampName);
            if (!forceRenew && File.Exists(file))
            {
                try
                {
                    var encrypted = Convert.FromBase64String(File.ReadAllText(file));
                    connStr = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
                    if (!forceValidate || ValidateSqlConnectionString(stampName, connStr))
                    {
                        SqlConnectionStrings[stampName] = connStr;
                        return connStr;
                    }
                }
                catch (Exception)
                {

                }
            }

            var db = "hosting";
            if (stampName.StartsWith("gm-"))
            {
                db = "geomaster";
            }
            else if (stampName.StartsWith("gr-"))
            {
                db = "georegionservice";
            }

            try
            {
                var privateDefinition = string.Format(@"\\AntaresDeployment\PublicLockbox\{0}\developer.definitions", stampName);
                if (File.Exists(privateDefinition))
                {
                    connStr = ReadFromPrivateDefinition(privateDefinition);
                }
                else
                {
                    //connStr = AntaresDiagnosticsCommon.Value.GetHostingDBConnectionStringForStamp(stampName, db);
                    connStr = GetConnectionStringAsync(db, stampName).Result;
                }

                if (ValidateSqlConnectionString(stampName, connStr))
                {
                    var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(connStr), null, DataProtectionScope.CurrentUser);
                    File.WriteAllText(file, Convert.ToBase64String(encrypted));
                    SqlConnectionStrings[stampName] = connStr;
                    return connStr;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("R/O SqlConnection {0} failed with {1}", stampName, ex.Message);
                Console.WriteLine();
                if (forceRenew)
                {
                    throw;
                }
            }

            return null;
        }

        // <definitions xmlns="http://schemas.microsoft.com/configdefinitions/">
        //   <redefine name="SqlServer" value="m4lmuhf972.database.windows.net" />
        //   <redefine name="SqlAdminUser" value="cgillum" />
        //   <redefine name="SqlAdminPassword" value="@[password(Antares/cgillum/SqlAdminPassword)]" />
        private static string ReadFromPrivateDefinition(string definitionFile)
        {
            string sqlServer = null;
            string sqlAdminUser = null;
            string sqlAdminPassword = null;
            foreach (var elem in XDocument.Load(definitionFile)
                .Root
                    .Elements("{http://schemas.microsoft.com/configdefinitions/}redefine")
                        .Where(e => e.Attribute("name").Value == "SqlServer" ||
                                    e.Attribute("name").Value == "SqlAdminUser" ||
                                    e.Attribute("name").Value == "SqlAdminPassword"))
            {
                if (elem.Attribute("name").Value == "SqlServer")
                {
                    sqlServer = elem.Attribute("value").Value;
                }
                else if (elem.Attribute("name").Value == "SqlAdminUser")
                {
                    sqlAdminUser = elem.Attribute("value").Value;
                }
                else if (elem.Attribute("name").Value == "SqlAdminPassword")
                {
                    sqlAdminPassword = ReadSecretStore(elem.Attribute("value").Value.Split('(', ')')[1]);
                }
            }

            return string.Format("Data Source={0};Initial Catalog=hosting;User ID={1};Password={2}", sqlServer, sqlAdminUser, sqlAdminPassword);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadSecretStore(string secretPath)
        {
            using (var ssp = new StashServiceProxy())
            {
                //string url = "https://test.secretstore.core.windows.net";
                string url = "https://test.secretstore.core.azure-test.net";
                //string url = "https://test.secretstore.core.azure-test.net/CertSvc.svc";
                //string url = "https://test-standby.secretstore.core.azure-test.net";
                ssp.connect(url);
                if (ssp.LastError != null)
                {
                    throw new InvalidOperationException(string.Format("Connect to secret store failed with {0}.", ssp.LastError));
                }

                var storageKey = ssp.GetPasswordDecrypted(secretPath).Value.Password;
                if (ssp.LastError != null)
                {
                    throw new InvalidOperationException(string.Format("Read secret store failed with {0}.", ssp.LastError));
                }

                return storageKey;
            }
        }

        static bool ValidateSqlConnectionString(string stampName, string connstr)
        {
            try
            {
                using (var sqlConn = new SqlConnection(connstr))
                {
                    sqlConn.Open();
                    sqlConn.Close();
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("R/O SqlConnection {0} failed with {1}", stampName, ex.Message);
                Console.WriteLine();
                return false;
            }
        }

        private static async Task<AuthenticationResult> GetAuthenticationTokenAsync()
        {
            if (_authenticationToken == null || _authenticationToken.ExpiresOn <= DateTimeOffset.UtcNow)
            {
                string authority = string.Format("https://login.microsoftonline.com/{0}", "72f988bf-86f1-41af-91ab-2d7cd011db47");
                AuthenticationContext authenticationContext = new AuthenticationContext(authority);
                //if (this._cert == null)
                {
                    _authenticationToken = await authenticationContext.AcquireTokenAsync(BaseAddress, UserAuthAADClientId,
                        new Uri("http://wawshealth-client"), new PlatformParameters(PromptBehavior.Auto),
                        UserIdentifier.AnyUser, "amr_values=mfa");
                }
                //else
                //{
                //    ClientAssertionCertificate clientCertificate = new ClientAssertionCertificate(this.CertAuthAADClientId, this._cert);
                //    this._authenticationToken = await authenticationContext.AcquireTokenAsync(this.BaseAddress, clientCertificate);
                //}
            }

            return _authenticationToken;
        }

        static async Task<string> GetConnectionStringAsync(string connectionName, string stampNameOrDataSource) //, ConnectionFormat format = ConnectionFormat.Standard, string databaseModel = "DataProvider.Model.Hosting", bool forDataSource = false)
        {
            string text = string.Empty;
            try
            {
                text = await GetValueFromServiceAsync<string>("api/connection_strings", HttpMethod.Get, 
                    new KeyValuePair<string, string>("stampName", stampNameOrDataSource),
                    new KeyValuePair<string, string>("databaseName", connectionName),
                    new KeyValuePair<string, string>("client", GetClientInfo()));
            }
            catch (HttpException ex)
            {
                if (ex.GetHttpCode() == 404)
                {
                    throw new Exception(string.Format("Cannot find database connection string for {0} in {1}.", connectionName, stampNameOrDataSource));
                }

                throw;
            }

            if (string.Equals(connectionName, "HostingMaster", StringComparison.InvariantCultureIgnoreCase))
            {
                text = new SqlConnectionStringBuilder(text)
                {
                    InitialCatalog = "master"
                }.ConnectionString;
            }

            //if (format == ConnectionFormat.EntityFramework)
            //{
            //    text = Utils.FormatToEntityFramework(text, databaseModel);
            //}

            return text;
        }

        private static async Task<T> GetValueFromServiceAsync<T>(string url, HttpMethod httpMethod, params KeyValuePair<string, string>[] nameValueCollection)
        {
            var authenticationResult = await GetAuthenticationTokenAsync();
            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authenticationResult.AccessToken);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "HAH " + GetClientInfo());

                var requestUrl = BaseAddress + url;
                if (httpMethod == HttpMethod.Get)
                {
                    requestUrl += "?" + string.Join("&", nameValueCollection.Select(p => string.Format("{0}={1}", HttpUtility.UrlEncode(p.Key), HttpUtility.UrlEncode(p.Value))));
                }

                var requestMessage = new HttpRequestMessage(httpMethod, requestUrl);
                if (httpMethod != HttpMethod.Get)
                {
                    requestMessage.Content = new FormUrlEncodedContent(nameValueCollection);
                }

                using (var httpResponseMessage = await httpClient.SendAsync(requestMessage))
                {
                    var value = await httpResponseMessage.Content.ReadAsStringAsync();
                    if (!httpResponseMessage.IsSuccessStatusCode)
                    {
                        var ex = new HttpException((int)httpResponseMessage.StatusCode, string.Format("Fail to send request to {0}. StatuCod {1}", BaseAddress, httpResponseMessage.StatusCode));
                        ex.Data["Content"] = value;
                        if (httpResponseMessage.Headers.RetryAfter != null)
                        {
                            ex.Data["Retry-After"] = httpResponseMessage.Headers.RetryAfter;
                        }
                        throw ex;
                    }

                    var javaScriptSerializer = new JavaScriptSerializer();
                    return javaScriptSerializer.Deserialize<T>(value);
                }
            }
        }

        private static string GetClientInfo()
        {
            if (_clientInfo == null)
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                string text = string.Empty;
                IPAddress iPAddress = hostEntry.AddressList.FirstOrDefault((IPAddress i) => i.AddressFamily == AddressFamily.InterNetwork);
                if (iPAddress != null)
                {
                    text = iPAddress.ToString();
                }
                string machineName = System.Environment.MachineName;
                string text2 = Process.GetCurrentProcess().ProcessName;
                //if (text2.Equals("w3wp", StringComparison.OrdinalIgnoreCase))
                //{
                //    text2 = HostingEnvironment.SiteName;
                //}
                //string text3 = (this._cert == null) ? WindowsIdentity.GetCurrent().Name.Replace("\\", string.Empty) : this._cert.Thumbprint;
                string text3 = WindowsIdentity.GetCurrent().Name.Replace("\\", string.Empty); // : this._cert.Thumbprint;
                _clientInfo = string.Format("{0}|{1}|{2}|{3}",
                    text2,
                    machineName,
                    text,
                    text3
                );
            }
            return _clientInfo;
        }
    }

    public class SetConsoleColor : IDisposable
    {
        ConsoleColor _previous;

        public SetConsoleColor(ConsoleColor color)
        {
            _previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
        }

        public void Dispose()
        {
            Console.ForegroundColor = _previous;
        }
    }
}
