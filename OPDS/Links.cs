﻿/***********************************************************
 * This file is a part of TinyOPDS server project
 * 
 * Copyright (c) 2013 SeNSSoFT
 * All rights reserved.
 *
 * This code is licensed under the Microsoft Public License, 
 * see http://tinyopds.codeplex.com/license for the details.
 *
 * This module defines static OPDS links
 * 
 ************************************************************/

using System.Xml.Linq;

namespace TinyOPDS.OPDS
{
    public class Links
    {
        public static XElement opensearch = new XElement("link", 
                                                new XAttribute("href","http://www.w3.org/2005/Atom"),
                                                new XAttribute("rel","search"),
                                                new XAttribute("type","application/opensearchdescription+xml"));

        public static XElement search = new XElement("link", 
                                                new XAttribute("href","http://{$HOST}/search?searchTerm={searchTerms}"),
                                                new XAttribute("rel","search"),
                                                new XAttribute("type","application/atom+xml;profile=opds-catalog"));

        public static XElement start = new XElement("link",
                                                new XAttribute("href", "http://{$HOST}"),
                                                new XAttribute("rel","start"),
                                                new XAttribute("type","application/atom+xml"));

        public static XElement self = new XElement("link",
                                                new XAttribute("href", "http://{$HOST}"),
                                                new XAttribute("rel","self"),
                                                new XAttribute("type","application/atom+xml"));
    }
}
