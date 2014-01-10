﻿/*
  Copyright (c) 2011-2013, HL7, Inc.
  All rights reserved.
  
  Redistribution and use in source and binary forms, with or without modification, 
  are permitted provided that the following conditions are met:
  
   * Redistributions of source code must retain the above copyright notice, this 
     list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above copyright notice, 
     this list of conditions and the following disclaimer in the documentation 
     and/or other materials provided with the distribution.
   * Neither the name of HL7 nor the names of its contributors may be used to 
     endorse or promote products derived from this software without specific 
     prior written permission.
  
  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND 
  ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED 
  WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, 
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT 
  NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR 
  PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
  WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
  POSSIBILITY OF SUCH DAMAGE.
  

*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using Hl7.Fhir.Model;
using System.Xml.Linq;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Hl7.Fhir.Support;
using Hl7.Fhir.Rest;

namespace Hl7.Fhir.Serialization
{
    internal static class BundleJsonParser
    {
        internal const string JATOM_VERSION = "version";
        internal const string JATOM_DELETED = "deleted";

        private static int? intValueOrNull(JToken attr)
        {
            if (attr == null) return null;

            return attr.Value<int?>();
        }

      
        private static DateTimeOffset? instantOrNull(JToken attr)
        {
            if (attr == null) return null;

            return PrimitiveTypeConverter.Convert<DateTimeOffset>(attr.Value<string>());
        }

        internal static Bundle Load(JsonReader reader)
        {
            JObject feed;

            try
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Decimal;
                feed = JObject.Load(reader);
            }
            catch (Exception exc)
            {
                throw Error.Format("Exception while parsing feed: " + exc.Message);
            }

            Bundle result;

            try
            {
                result = new Bundle()
                {
                    Title = feed.Value<string>(BundleXmlParser.XATOM_TITLE),
                    LastUpdated = instantOrNull(feed[BundleXmlParser.XATOM_UPDATED]),
                    Id = SerializationUtil.UriValueOrNull(feed[BundleXmlParser.XATOM_ID]),
                    Links = getLinks(feed[BundleXmlParser.XATOM_LINK]),
                    Tags = TagListParser.ParseTags(feed[BundleXmlParser.XATOM_CATEGORY]),
                    AuthorName = feed[BundleXmlParser.XATOM_AUTHOR] as JArray != null ?
                                feed[BundleXmlParser.XATOM_AUTHOR]
                                    .Select(auth => auth.Value<string>(BundleXmlParser.XATOM_AUTH_NAME))
                                    .FirstOrDefault()
                                : null,
                    AuthorUri = feed[BundleXmlParser.XATOM_AUTHOR] as JArray != null ?
                                feed[BundleXmlParser.XATOM_AUTHOR]
                                    .Select(auth => auth.Value<string>(BundleXmlParser.XATOM_AUTH_URI))
                                    .FirstOrDefault() : null,
                    TotalResults = intValueOrNull(feed[BundleXmlParser.XATOM_TOTALRESULTS])
                };
            }
            catch (Exception exc)
            {
                throw Error.Format("Exception while parsing json feed attributes: " + exc.Message);                
            }

            var entries = feed[BundleXmlParser.XATOM_ENTRY];
            if (entries != null)
            {
                if (!(entries is JArray))
                {
                    throw Error.Format("The json feed contains a single entry, instead of an array");
                }

                result.Entries = loadEntries((JArray)entries, result);
            }

            return result;
        }

        private static IList<BundleEntry> loadEntries(JArray entries, Bundle parent)
        {
            var result = new List<BundleEntry>();

            foreach (var entry in entries)
            {
                if (entry.Type != JTokenType.Object)
                    throw Error.Format("Expected a complex object when reading an entry");

                var loaded = loadEntry((JObject)entry);
                if(entry != null) result.Add(loaded);
            }

            return result;
        }

        internal static BundleEntry LoadEntry(JsonReader reader)
        {
            JObject entry;
            reader.DateParseHandling = DateParseHandling.None;
            reader.FloatParseHandling = FloatParseHandling.Decimal;

            try
            {
                entry = JObject.Load(reader);
            }
            catch (Exception exc)
            {
                throw Error.Format("Exception while parsing json for entry: " + exc.Message);
            }

            return loadEntry(entry);
        }


        private static BundleEntry loadEntry(JObject entry)
        {
            BundleEntry result;

            try
            {
                if (entry[JATOM_DELETED] != null)
                {
                    result = new DeletedEntry();
                    result.Id = SerializationUtil.UriValueOrNull(entry[BundleXmlParser.XATOM_ID]);
                }
                else
                {
                    var content = entry[BundleXmlParser.XATOM_CONTENT];
                    var id = SerializationUtil.UriValueOrNull(entry[BundleXmlParser.XATOM_ID]);

                    if (content != null)
                    {
                        var parsed = getContents(content);
                        if (parsed != null)
                            result = ResourceEntry.Create(parsed);
                        else
                            throw Error.Format("BundleEntry has a content element without content");
                    }
                    else
                    {
                        result = new ResourceEntry();

                        //// No content, try to figure out the resource type from the id
                        //ResourceIdentity rid = new ResourceIdentity(id);
                        //if (rid.Collection != null)
                        //{
                        //    //TODO: this won't really work when new subtypes have been installedin ModelInspector for the resources
                        //    result = ResourceEntry.Create(Type.GetType(rid.Collection));                            
                        //}
                        //else
                        //    throw Error.Format("BundleEntry has empty content and no id with resource name embedden: cannot determine Resource type in parser.");
                    }

                    result.Id = id;
                }
                
                result.Links = getLinks(entry[BundleXmlParser.XATOM_LINK]);
                result.Tags = TagListParser.ParseTags(entry[BundleXmlParser.XATOM_CATEGORY]);

                if (result is DeletedEntry)
                    ((DeletedEntry)result).When = instantOrNull(entry[JATOM_DELETED]);
                else
                {
                    var re = (ResourceEntry)result;
                    re.Title = entry.Value<string>(BundleXmlParser.XATOM_TITLE);
                    re.LastUpdated = instantOrNull(entry[BundleXmlParser.XATOM_UPDATED]);
                    re.Published = instantOrNull(entry[BundleXmlParser.XATOM_PUBLISHED]);
                    re.AuthorName = entry[BundleXmlParser.XATOM_AUTHOR] as JArray != null ?
                        entry[BundleXmlParser.XATOM_AUTHOR]
                            .Select(auth => auth.Value<string>(BundleXmlParser.XATOM_AUTH_NAME))
                            .FirstOrDefault() : null;
                    re.AuthorUri = entry[BundleXmlParser.XATOM_AUTHOR] as JArray != null ?
                        entry[BundleXmlParser.XATOM_AUTHOR]
                            .Select(auth => auth.Value<string>(BundleXmlParser.XATOM_AUTH_URI))
                            .FirstOrDefault() : null;
                }
            }
            catch (Exception exc)
            {
                throw Error.Format("Exception while reading entry: " + exc.Message);
            }

            return result;
        }

      
        private static UriLinkList getLinks(JToken token)
        {
            var result = new UriLinkList();
            var links = token as JArray;

            if (links != null)
            {
                foreach (var link in links)
                {
                    var uri = SerializationUtil.UriValueOrNull(link[BundleXmlParser.XATOM_LINK_HREF]);

                    if (uri != null)
                        result.Add(new UriLinkEntry
                        {
                            Rel = link.Value<string>(BundleXmlParser.XATOM_LINK_REL),
                            Uri = uri
                        });
                }
            }

            return result;
        }

        private static Resource getContents(JToken token)
        {
            return (Resource)(new ResourceReader(new JsonDomFhirReader(token)).Deserialize());
        }
    }
}