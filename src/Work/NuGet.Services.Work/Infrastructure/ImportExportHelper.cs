﻿// Adapted from NuGetGallery.sln which is adapted from http://code.msdn.microsoft.com/windowsazure/Windows-Azure-SQL-Database-5eb17fe2

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Xml;
using System.Web;
using System.Runtime.Serialization;
using System.Threading;
using System.Diagnostics.CodeAnalysis;
using NuGet.Services.Work.DACWebService;
using System.Diagnostics.Tracing;
using NuGet.Services.Work.Jobs;

namespace WASDImportExport
{
    class ImportExportHelper
    {
        private SyncDatacenterEventSource _log;

        public string EndPointUri { get; set; }
        public string StorageKey { get; set; }
        public string ServerName { get; set; }
        public string DatabaseName { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }

        public ImportExportHelper(SyncDatacenterEventSource log)
        {
            _log = log;
            EndPointUri = "";
            ServerName = "";
            StorageKey = "";
            DatabaseName = "";
            UserName = "";
            Password = "";
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public string DoExport(string blobUri, bool whatIf, bool async = true)
        {
            _log.Information("Starting SQL DAC Export Operation");
            string requestGuid = null;
            bool exportComplete = false;
            string exportedBlobPath = null;

            //Setup Web Request for Export Operation
            WebRequest webRequest = WebRequest.Create(this.EndPointUri + @"/Export");
            webRequest.Method = WebRequestMethods.Http.Post;
            webRequest.ContentType = @"application/xml";

            //Create Web Request Inputs - Blob Storage Credentials and Server Connection Info
            ExportInput exportInputs = new ExportInput
            {
                BlobCredentials = new BlobStorageAccessKeyCredentials
                {
                    StorageAccessKey = this.StorageKey,
                    Uri = String.Format(blobUri, this.DatabaseName, DateTime.UtcNow.Ticks.ToString())
                },
                ConnectionInfo = new ConnectionInfo
                {
                    ServerName = this.ServerName,
                    DatabaseName = this.DatabaseName,
                    UserName = this.UserName,
                    Password = this.Password
                }
            };

            //Perform Web Request
            DataContractSerializer dataContractSerializer = new DataContractSerializer(exportInputs.GetType());
            _log.Information(String.Format("http POST {0}", webRequest.RequestUri.AbsoluteUri));
            if (whatIf)
            {
                _log.Information("Would have sent:");

                using (var strm = new MemoryStream())
                {
                    dataContractSerializer.WriteObject(strm, exportInputs);
                    strm.Flush();
                    strm.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(strm))
                    {
                        _log.Information(reader.ReadToEnd());
                    }
                }
                return null;
            }
            else
            {
                _log.Information("Sending: ");
                using (var strm = new MemoryStream())
                {
                    dataContractSerializer.WriteObject(strm, exportInputs);
                    strm.Flush();
                    strm.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(strm))
                    {
                        _log.Information(reader.ReadToEnd());
                    }
                }

                _log.Information("Making Web Request For Export Operation...");
                Stream webRequestStream = webRequest.GetRequestStream();
                dataContractSerializer.WriteObject(webRequestStream, exportInputs);
                webRequestStream.Close();

                //Get Response and Extract Request Identifier
                WebResponse webResponse = null;
                XmlReader xmlStreamReader = null;

                try
                {
                    //Initialize the WebResponse to the response from the WebRequest
                    webResponse = webRequest.GetResponse();

                    xmlStreamReader = XmlReader.Create(webResponse.GetResponseStream());
                    xmlStreamReader.ReadToFollowing("guid");
                    requestGuid = xmlStreamReader.ReadElementContentAsString();
                    _log.Information(String.Format("Export Request '{0}' submitted", requestGuid));

                    //Get Export Operation Status
                    string last = null;
                    while (!async || !exportComplete)
                    {
                        List<StatusInfo> statusInfoList = CheckRequestStatus(requestGuid);
                        var status = statusInfoList.FirstOrDefault().Status;
                        if (!String.Equals(last, status, StringComparison.OrdinalIgnoreCase))
                        {
                            _log.Information(status);
                        }
                        last = status;

                        if (statusInfoList.FirstOrDefault().Status == "Failed")
                        {
                            _log.Information(String.Format("Database export failed: {0}", statusInfoList.FirstOrDefault().ErrorMessage));
                            exportComplete = true;
                        }

                        if (statusInfoList.FirstOrDefault().Status == "Completed")
                        {
                            exportedBlobPath = statusInfoList.FirstOrDefault().BlobUri;
                            _log.Information(String.Format("Export Complete - Database exported to: {0}", exportedBlobPath));
                            exportComplete = true;
                        }
                        Thread.Sleep(5 * 1000);
                    }
                    return requestGuid;
                }
                catch (WebException responseException)
                {
                    _log.Error(String.Format("Request Falied:{0}", responseException.Message));
                    if (responseException.Response != null)
                    {
                        _log.Error(String.Format("Status Code: {0}", ((HttpWebResponse)responseException.Response).StatusCode));
                        _log.Error(String.Format("Status Description: {0}", ((HttpWebResponse)responseException.Response).StatusDescription));
                    }
                    return null;
                }
            }
        }

        public bool DoImport(string blobUri, bool whatIf, bool async = true)
        {
            _log.Information(String.Format("Starting Import Operation - {0}\n\r", DateTime.Now));
            string requestGuid = null;
            bool importComplete = false;

            //Setup Web Request for Import Operation
            WebRequest webRequest = WebRequest.Create(this.EndPointUri + @"/Import");
            webRequest.Method = WebRequestMethods.Http.Post;
            webRequest.ContentType = @"application/xml";

            //Create Web Request Inputs - Database Size & Edition, Blob Store Credentials and Server Connection Info
            ImportInput importInputs = new ImportInput
            {
                AzureEdition = "Web",
                DatabaseSizeInGB = 1,
                BlobCredentials = new BlobStorageAccessKeyCredentials
                {
                    StorageAccessKey = this.StorageKey,
                    Uri = String.Format(blobUri, this.DatabaseName, DateTime.UtcNow.Ticks.ToString())
                },
                ConnectionInfo = new ConnectionInfo
                {
                    ServerName = this.ServerName,
                    DatabaseName = this.DatabaseName,
                    UserName = this.UserName,
                    Password = this.Password
                }
            };

            //Perform Web Request
            _log.Information("Making Web Request for Import Operation...");
            Stream webRequestStream = webRequest.GetRequestStream();
            DataContractSerializer dataContractSerializer = new DataContractSerializer(importInputs.GetType());
            dataContractSerializer.WriteObject(webRequestStream, importInputs);
            webRequestStream.Close();

            //Get Response and Extract Request Identifier
            _log.Information("Serializing response and extracting guid...");
            WebResponse webResponse = null;
            XmlReader xmlStreamReader = null;

            try
            {
                //Initialize the WebResponse to the response from the WebRequest
                webResponse = webRequest.GetResponse();

                xmlStreamReader = XmlReader.Create(webResponse.GetResponseStream());
                xmlStreamReader.ReadToFollowing("guid");
                requestGuid = xmlStreamReader.ReadElementContentAsString();
                _log.Information(String.Format("Request Guid: {0}", requestGuid));

                //Get Status of Import Operation
                while (!async && !importComplete)
                {
                    _log.Information("Checking status of Import...");
                    List<StatusInfo> statusInfoList = CheckRequestStatus(requestGuid);
                    _log.Information(statusInfoList.FirstOrDefault().Status);

                    if (statusInfoList.FirstOrDefault().Status == "Failed")
                    {
                        _log.Information(String.Format("Database import failed: {0}", statusInfoList.FirstOrDefault().ErrorMessage));
                        importComplete = true;
                    }

                    if (statusInfoList.FirstOrDefault().Status == "Completed")
                    {
                        _log.Information(String.Format("Import Complete - Database imported to: {0}\n\r", statusInfoList.FirstOrDefault().DatabaseName));
                        importComplete = true;
                    }
                }
                return importComplete;
            }
            catch (WebException responseException)
            {
                _log.Error(String.Format("Request Falied: {0}", responseException.Message));
                {
                    _log.Error(String.Format("Status Code: {0}", ((HttpWebResponse)responseException.Response).StatusCode));
                    _log.Error(String.Format("Status Description: {0}\n\r", ((HttpWebResponse)responseException.Response).StatusDescription));
                }

                return importComplete;
            }
        }

        public List<StatusInfo> CheckRequestStatus(string requestGuid)
        {
            WebRequest webRequest = WebRequest.Create(this.EndPointUri + string.Format("/Status?servername={0}&username={1}&password={2}&reqId={3}",
                    HttpUtility.UrlEncode(this.ServerName),
                    HttpUtility.UrlEncode(this.UserName),
                    HttpUtility.UrlEncode(this.Password),
                    HttpUtility.UrlEncode(requestGuid)));

            webRequest.Method = WebRequestMethods.Http.Get;
            webRequest.ContentType = @"application/xml";
            WebResponse webResponse = webRequest.GetResponse();
            XmlReader xmlStreamReader = XmlReader.Create(webResponse.GetResponseStream());
            DataContractSerializer dataContractSerializer = new DataContractSerializer(typeof(List<StatusInfo>));

            return (List<StatusInfo>)dataContractSerializer.ReadObject(xmlStreamReader, true);
        }
    }
}
