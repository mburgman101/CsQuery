﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace CsQuery.HtmlParser
{
    /// <summary>
    /// Reference data about HTML tags and attributes;
    /// methods to test tokens for certain properties;
    /// and tokenizer
    /// </summary>
    public static class HtmlData
    {
        #region constants and data

        // when false, will use binary data for the character set

#if DEBUG_PATH
        public static bool Debug = true;
        public const int pathIdLength = 3;
        public const char indexSeparator = '>';
#else
        public static bool Debug = false;
        /// <summary>
        /// Length of each node's path ID (in characters), sets a limit on the number of child nodes before a reindex
        /// is required. For most cases, a small number will yield better performance. In production we probably can get
        /// away with just 1 (meaning a char=65k possible values). 
        /// </summary>
        public const int pathIdLength = 1;
        /// <summary>
        /// The character used to separate the unique part of an index entry from its path. When debugging
        /// it is useful to have a printable character. Otherwise we want something that is guaranteed to be
        /// a unique stop character.
        /// </summary>
        public const char indexSeparator = (char)1;
#endif
        

        /// Hardcode some tokens to improve performance when referring to them often
        
        public const ushort ClassAttrId = 3;
        public const ushort ValueAttrId=4;
        public const ushort IDAttrId=5;
        
        public const ushort tagSCRIPT= 6;
        public const ushort tagTEXTAREA= 7;
        public const ushort tagSTYLE = 8;
        public const ushort tagINPUT = 9;
        public const ushort tagSELECT = 10;
        public const ushort tagOPTION = 11;
        public const ushort tagP = 12;
        public const ushort tagTR=13;
        public const ushort tagTD = 14;
        public const ushort tagTH = 15;
        public const ushort tagHEAD = 16;
        public const ushort tagBODY = 17;
        public const ushort tagDT = 18;
        public const ushort tagCOLGROUP = 19;
        public const ushort tagDD = 20;
        public const ushort tagLI = 21;
        public const ushort tagDL = 22;
        public const ushort tagTABLE = 23;
        public const ushort tagOPTGROUP = 24;
        public const ushort tagUL = 25;
        public const ushort tagOL = 26;
        public const ushort tagTBODY = 27;
        public const ushort tagTFOOT = 28;
        public const ushort tagTHEAD = 29;

        public const ushort SelectedAttrId=30;
        public const ushort ReadonlyAttrId=31;
        public const ushort CheckedAttrId=32;

        private const ushort maxHardcodedTokenId = 32;

        // HTML spec for whitespace
        // U+0020 SPACE, U+0009 CHARACTER TABULATION (tab), U+000A LINE FEED (LF), U+000C FORM FEED (FF), and U+000D CARRIAGE RETURN (CR).

        public static char[] Whitespace = new char[] { '\x0020', '\x0009', '\x000A', '\x000C', '\x000D' };
        
        // U+0022 QUOTATION MARK characters ("), U+0027 APOSTROPHE characters ('), U+003D EQUALS SIGN characters (=), 
        // U+003C LESS-THAN SIGN characters (<), U+003E GREATER-THAN SIGN characters (>), or U+0060 GRAVE ACCENT characters (`),
        // and must not be the empty string.}
        
        public static char[] MustBeQuoted = new char[] { '/','\x0022', '\x0027', '\x003D', '\x003C', '\x003E', '\x0060' };
        public static char[] MustBeQuotedAll;

        // Things that can be in a CSS number

        public static HashSet<char> NumberChars = new HashSet<char>("-+0123456789.,");

        // Things that are allowable unit strings in a CSS style.

        public static HashSet<string> Units = new HashSet<string>(new string[] { "%", "in", "cm", "mm", "em", "ex", "pt", "pc", "px" });

        /// <summary>
        /// Fields used internally
        /// </summary>

        private static ushort nextID = 2;
        private static List<string> Tokens = new List<string>();
        private static Dictionary<string, ushort> TokenIDs;
        private static object locker = new Object();
        
        // Constants for path encoding functions

        private static string defaultPadding;
        
        // The character set used to generate path IDs
        // For production use, this is replaced with a string of all ansi characters

        private static char[] baseXXchars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToArray();
        private static int encodingLength; // set in constructor
        private static int maxPathIndex;

        // This will be a lookup table where each value contains binary flags indicating what
        // properties are true for that value. We fix a size that's at an even binary boundary
        // so we can mask it for fast comparisons. If the number of tags with data exceeded 64
        // this can just increase; anything above the last used slot will just be 0.

        private static ushort[] TokenMetadata = new ushort[256];
        
        // (256 * 256) & ~256
        // this is the mask to test if an ID is in outside short list
        private const ushort NonSpecialTokenMask = (ushort)65280;
        
        #endregion

        #region constructor

        static HtmlData()
        {
            // For path encoding - when in production mode use a single character value for each path ID. This lets us avoid doing 
            // a division for indexing to determine path depth, and is just faster for a lot of reasons. This should be plenty
            // of tokens: things that are tokenized are tag names style names, class names, attribute names (not values), and ID 
            // values. You'd be hard pressed to exceed this limit (65k for a single level) on one single web page. 
            // (Famous last words right?)

            #if !DEBUG_PATH
                baseXXchars = new char[65533];
                for (ushort i = 0; i < 65533; i++)
                {
                    baseXXchars[i] = (char)(i+2);
                }
            #endif

            encodingLength = baseXXchars.Length;
            defaultPadding="";
            for (int i = 1; i < pathIdLength; i++)
            {
                defaultPadding=defaultPadding+"0";
            }
            maxPathIndex = (int)Math.Pow(encodingLength,pathIdLength) -1;

            MustBeQuotedAll = new char[Whitespace.Length + MustBeQuoted.Length];
            MustBeQuoted.CopyTo(MustBeQuotedAll, 0);
            Whitespace.CopyTo(MustBeQuotedAll, MustBeQuoted.Length);

            // these elements can never have html children.

            string[] noChildHtmlAllowed = new string[]{
                // may have text content

                "SCRIPT","TEXTAREA","STYLE"

            };

            string[] noChildrenAllowed = new string[] {
                "BASE","BASEFONT","FRAME","LINK","META","AREA","COL","HR","PARAM",
                "IMG","INPUT","BR", "!DOCTYPE","!--", "COMMAND", "EMBED","KEYGEN","SOURCE","TRACK","WBR"

            };
    
            
            // these elements will cause certain tags to be closed automatically; 
            // this is very important for layout.
               
            // 6-19-2012: removed "object" - object is inline.

            string[] blockElements = new string[]{
                "BODY","BR","ADDRESS","BLOCKQUOTE","CENTER","DIV","DIR","FORM","FRAMESET",
                "H1","H2","H3","H4","H5","H6","HR",
                "ISINDEX","LI","NOFRAMES","NOSCRIPT",
                "OL","P","PRE","TABLE","TR","TEXTAREA","UL",
                
                // html5 additions
                "ARTICLE","ASIDE","BUTTON","CANVAS","CAPTION","COL","COLGROUP","DD","DL","DT","EMBED",
                "FIELDSET","FIGCAPTION","FIGURE","FOOTER","HEADER","HGROUP","PROGRESS","SECTION",
                "TBODY","THEAD","TFOOT","VIDEO",
                
                // really old
                "APPLET","LAYER","LEGEND"
            };

            // these elements are boolean; they do not have a value other than present or missing. They
            // are really "properties" but we don't have a distinction between properties, and a rendered
            // attribute. (It makes no sense in CsQuery; the only thing that matters when the DOM is
            // rendered is whether the attribute is present. This could change if the DOM were used with
            // a javascript engine, though, e.g. to simulate a browser)

            string[] booleanAttributes = new string[] {
                "AUTOBUFFER", "AUTOFOCUS", "AUTOPLAY", "ASYNC", "CHECKED", "COMPACT", "CONTROLS", 
                "DECLARE", "DEFAULTMUTED", "DEFAULTSELECTED", "DEFER", "DISABLED", "DRAGGABLE", 
                "FORMNOVALIDATE", "HIDDEN", "INDETERMINATE", "ISMAP", "ITEMSCOPE","LOOP", "MULTIPLE",
                "MUTED", "NOHREF", "NORESIZE", "NOSHADE", "NOWRAP", "NOVALIDATE", "OPEN", "PUBDATE", 
                "READONLY", "REQUIRED", "REVERSED", "SCOPED", "SEAMLESS", "SELECTED", "SPELLCHECK", 
                "TRUESPEED"," VISIBLE"
            };


            string[] autoClosingTags = new string[] {
                "P","LI","TR","TD","TH","THEAD","TBODY","TFOOT","OPTION","HEAD","DT","DD","COLGROUP","OPTGROUP"
            };
            
            

            TokenIDs = new Dictionary<string, ushort>();
            // where Style used to be
            TokenID("unused"); //2

            TokenID("class"); //3
            // inner text allowed
            TokenID("value"); //4
            TokenID("id"); //5

            //noInnerHtmlIDFirst = nextID;
            // the node types that have inner content which is not parsed as HTML ever
            TokenID("script"); //6
            TokenID("textarea"); //7
            TokenID("style"); //8


            TokenID("input"); //9
            TokenID("select"); //10
            TokenID("option"); //11

            TokenID("p"); //12
            TokenID("tr"); //13
            TokenID("td"); //14
            TokenID("th"); //15
            TokenID("head"); //16
            TokenID("body"); //17
            TokenID("dt"); //18
            TokenID("colgroup"); //19
            TokenID("dd"); //20
            TokenID("li"); //21
            TokenID("dl"); //22
            TokenID("table"); //23
            TokenID("optgroup"); //24
            TokenID("ul"); //25
            TokenID("ol"); //26
            TokenID("tbody"); //27
            TokenID("tfoot"); //28
            TokenID("thead"); //29
            
            TokenID("selected"); //30
            TokenID("readonly"); //31 
            TokenID("checked"); //32 

            if (nextID != maxHardcodedTokenId+1)
            {
                throw new InvalidOperationException("Something went wrong with the constant map in DomData");
            }

            // create the binary lookup table of tag metadata

            PopulateTokenHashset(noChildHtmlAllowed);
            PopulateTokenHashset(noChildrenAllowed);
            PopulateTokenHashset(blockElements);
            PopulateTokenHashset(booleanAttributes);
            PopulateTokenHashset(autoClosingTags);


            // reset nextId to beyond the boundary of the metadata array
            
            // Fill out the list of tokens to the boundary of the metadata array so the indices align

            while (nextID < (ushort)TokenMetadata.Length)
            {
                Tokens.Add(null);
                nextID++;
            }

            // no element children allowed but text children are

            setBit(noChildHtmlAllowed, TokenProperties.HtmlChildrenNotAllowed );

            // no children whatsoever

            setBit(noChildrenAllowed, TokenProperties.ChildrenNotAllowed | TokenProperties.HtmlChildrenNotAllowed);

            
            setBit(autoClosingTags, TokenProperties.AutomaticClose);          
            setBit(blockElements, TokenProperties.BlockElement);
            setBit(booleanAttributes, TokenProperties.BooleanProperty);

        }
        private static HashSet<ushort> PopulateTokenHashset(IEnumerable<string> tokens)
        {
            var set = new HashSet<ushort>();
            foreach (var item in tokens)
            {
                set.Add(TokenID(item));
            }
            return set;
        }
        #endregion

        #region public methods

        /// <summary>
        /// A list of all keys (tokens) created
        /// </summary>
        public static IEnumerable<string> Keys
        {
            get
            {
                return Tokens;
            }
        }

        /// <summary>
        /// This type does not allow HTML children. Some of these types may allow text but not HTML.
        /// </summary>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        public static bool HtmlChildrenNotAllowed(ushort nodeId)
        {
            return (nodeId & NonSpecialTokenMask) == 0 &&
                (TokenMetadata[nodeId] & (ushort)TokenProperties.HtmlChildrenNotAllowed) > 0;

        }

        /// <summary>
        /// This type does not allow HTML children. Some of these types may allow text but not HTML.
        /// </summary>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        public static bool HtmlChildrenNotAllowed(string nodeName)
        {
            return HtmlChildrenNotAllowed(TokenID(nodeName));
        }

        /// <summary>
        /// Text is allowed within this node type. Is includes all types that also permit HTML.
        /// </summary>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        public static bool InnerTextAllowed(ushort nodeId)
        {
            // nodeId & NonSpecialTokenMask returns zero for tokens that are in the short list.
            // anything outside the short list (or not matching special properties) os ok - 
            // innertextallowed is the default
            
            return (nodeId & NonSpecialTokenMask)!=0 ||
                (TokenMetadata[nodeId] & (ushort)TokenProperties.ChildrenNotAllowed)== 0;
        }
        public static bool InnerTextAllowed(string nodeName)
        {
            return InnerTextAllowed(TokenID(nodeName));
        }
        public static bool IsBlock(ushort nodeId)
        {

            return (nodeId & NonSpecialTokenMask)== 0 &&
                (TokenMetadata[nodeId] & (ushort)TokenProperties.BlockElement) != 0;
        }
        public static bool IsBlock(string nodeName)
        {
            return IsBlock(TokenID(nodeName));
        }
        /// <summary>
        /// The attribute is a boolean type
        /// </summary>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        public static bool IsBoolean(ushort nodeId)
        {
            return (nodeId & NonSpecialTokenMask) == 0 &&
                  (TokenMetadata[nodeId] & (ushort)TokenProperties.BooleanProperty) != 0;
        }
        /// <summary>
        /// The attribute is a boolean type
        /// </summary>
        /// <param name="nodeName"></param>
        /// <returns></returns>
        public static bool IsBoolean(string nodeName)
        {
            return IsBoolean(TokenID(nodeName));
        }


        public static ushort TokenID(string tokenName)
        {
        
            if (String.IsNullOrEmpty(tokenName))
            {
                return 0;
            }
            return TokenIDImpl(tokenName.ToLower());
            
        }

        /// <summary>
        /// Return a token ID for a name, adding to the index if it doesn't exist.
        /// When indexing tags and attributes, ignoreCase should be used
        /// </summary>
        /// <param name="tokenName"></param>
        /// <param name="ignoreCase"></param>
        /// <returns></returns>
        public static ushort TokenIDCaseSensitive(string tokenName)
        {
            if (String.IsNullOrEmpty(tokenName))
            {
                return 0;
            }
            return TokenIDImpl(tokenName);
        }
        /// <summary>
        /// Return a token ID for a name, adding to the index if it doesn't exist.
        /// When indexing tags and attributes, ignoreCase should be used
        /// </summary>
        /// <param name="tokenName"></param>
        /// <param name="ignoreCase"></param>
        /// <returns></returns>
        public static ushort TokenIDImpl(string tokenName)
        {
            ushort id;

            if (!TokenIDs.TryGetValue(tokenName, out id))
            {
                
                lock(locker) {
                    if (!TokenIDs.TryGetValue(tokenName, out id))
                    {
                        Tokens.Add(tokenName);
                        TokenIDs.Add(tokenName, nextID);
                        // if for some reason we go over 65,535, will overflow and crash. no need 
                        // to check
                        id = nextID++;
                    }
                }
            }
            return id;
        }
        /// <summary>
        /// Return a token name for an ID.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static string TokenName(ushort id)
        {
            return id <= 0 ? "" : Tokens[id-2];
        }

       

        /// <summary>
        /// Encode to base XX (defined in constants)
        /// </summary>
        /// <param name="number"></param>
        /// <returns></returns>
        public static string BaseXXEncode(int number)
        {

            // optimize for small numbers - this should mostly eliminate the ineffieciency of base62
            // encoding while giving benefits for storage space

            if (number < encodingLength)
            {
                return defaultPadding +baseXXchars[number];
            }

            if (number > maxPathIndex)
            {
                throw new OverflowException("Maximum number of child nodes (" + maxPathIndex + ") exceeded."); 
            }
            string sc_result = "";
            int num_to_encode = number;
            int i = 0;
            do
            {
                i++;
                sc_result = baseXXchars[(num_to_encode % encodingLength)] + sc_result;
                num_to_encode = ((num_to_encode - (num_to_encode % encodingLength)) / encodingLength);
                
            }
            while (num_to_encode != 0);
            
            return sc_result.PadLeft(pathIdLength, '0');
        }

        /// <summary>
        /// HtmlEncode the string (pass-thru to system; abstracted in case we want to change)
        /// </summary>
        /// <param name="html"></param>
        /// <returns></returns>
        public static string HtmlEncode(string html)
        {
            return System.Web.HttpUtility.HtmlEncode(html);

        }
        public static string HtmlDecode(string html)
        {
            return System.Web.HttpUtility.HtmlDecode(html);
        }


        /// <summary>
        /// Encode text as part of an attribute
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public static string AttributeEncode(string text)
        {
            string quoteChar;
            string attribute = AttributeEncode(text,
                CQ.DefaultDomRenderingOptions.HasFlag(DomRenderingOptions.QuoteAllAttributes),
                out quoteChar);
            return quoteChar + attribute + quoteChar;
        }
        
        /// <summary>
        /// Htmlencode a string, except for double-quotes, so it can be enclosed in single-quotes
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public static string AttributeEncode(string text, bool alwaysQuote, out string quoteChar)
        {
            if (text == "")
            {
                quoteChar = "\"";
                return "";
            }

            bool hasQuotes = text.IndexOf("\"") >= 0;
            bool hasSingleQuotes = text.IndexOf("'") >= 0;
            string result = text;
            if (hasQuotes || hasSingleQuotes)
            {

                //un-encode quotes or single-quotes when possible. When writing the attribute it will use the right one
                if (hasQuotes && hasSingleQuotes)
                {
                    result = result.Replace("'", "&#39;");
                    quoteChar = "\'";
                }
                else if (hasQuotes)
                {
                    quoteChar = "'";
                }
                else
                {
                    quoteChar = "\"";
                }
            }
            else
            {
                if (alwaysQuote)
                {
                    quoteChar = "\"";
                }
                else
                {
                    quoteChar = result.IndexOfAny(HtmlParser.HtmlData.MustBeQuotedAll) >= 0 ? "\"" : "";
                }
            }

            return result;
        }
        
        public static bool TagHasImplicitClose(string tag, string newTag)
        {
            return TagHasImplicitClose(TokenID(tag), TokenID(newTag));
        }


        // Some tags have inner HTML but are often not closed properly. There are two possible situations. A tag may not 
        // have a nested instance of itself, and therefore any recurrence of that tag implies the previous one is closed. 
        // Other tag closings are simply optional, but are not repeater tags (e.g. body, html). These should be handled
        // automatically by the logic that bubbles any closing tag to its parent if it doesn't match the current tag. The 
        // exception is <head> which technically does not require a close, but we would not expect to find another close tag
        // Complete list of optional closing tags: -</HTML>- </HEAD> -</BODY> -</P> -</DT> -</DD> -</LI> -</OPTION> -</THEAD> 
        // </TH> </TBODY> </TR> </TD> </TFOOT> </COLGROUP>
        //
        //  body, html will be closed automatically at the end of parsing and are also not required        

        /// <summary>
        /// Return true if open tagId is closed implicitly by an appearance of newTagId
        /// </summary>
        /// <param name="tagId"></param>
        /// <param name="newTagId"></param>
        /// <returns></returns>
        public static bool TagHasImplicitClose(ushort tagId, ushort newTagId)
        {
            if ((tagId & NonSpecialTokenMask) != 0 ||
                (TokenMetadata[tagId] & (ushort)TokenProperties.AutomaticClose) == 0)
            {
                return false;
            }

            switch (tagId)
            {
                case HtmlData.tagP:
                    // closing "p" tag is optional. Always close when a block element it returned.
                    return HtmlData.IsBlock(newTagId);
                case HtmlData.tagLI:
                    return newTagId == HtmlData.tagLI;
                case HtmlData.tagTR:
                    return newTagId == HtmlData.tagTR || newTagId == HtmlData.tagTABLE;
                case HtmlData.tagTD:
                    return newTagId == HtmlData.tagTD || newTagId == HtmlData.tagTR;
                case HtmlData.tagTH:
                    return newTagId == HtmlData.tagTH || newTagId == HtmlData.tagTR;
                // simple case: repeater-like tags should be closed by another occurence of itself
                case HtmlData.tagTHEAD:
                case HtmlData.tagTBODY:
                case HtmlData.tagTFOOT:
                    return newTagId == tagId || newTagId == HtmlData.tagTABLE;

                case HtmlData.tagOPTION:
                    return newTagId == HtmlData.tagOPTION || newTagId == HtmlData.tagSELECT;
                case HtmlData.tagHEAD:
                    return (newTagId == HtmlData.tagBODY);
                case HtmlData.tagDT:
                case HtmlData.tagDD:
                    return newTagId == HtmlData.tagDT || newTagId == HtmlData.tagDD || newTagId == HtmlData.tagDL;
                case HtmlData.tagCOLGROUP:
                    return newTagId == HtmlData.tagCOLGROUP || newTagId == HtmlData.tagTR || newTagId == HtmlData.tagTABLE
                        || newTagId == HtmlData.tagTHEAD || newTagId == HtmlData.tagTBODY || newTagId == HtmlData.tagTFOOT;
                case HtmlData.tagOPTGROUP:
                    return newTagId == HtmlData.tagOPTGROUP || newTagId == HtmlData.tagSELECT;
                default:
                    return false;

            }
        }
        
        #endregion

        #region private methods

        
        /// <summary>
        /// For each value in "tokens" (ignoring case) sets the specified bit in the reference table
        /// </summary>
        /// <param name="tokens"></param>
        /// <param name="bit"></param>
        private static void setBit(IEnumerable<string> tokens, TokenProperties bit)
        {
            foreach (var token in tokens)
            {
                setBit(TokenID(token), bit);
            }

        }

        /// <summary>
        /// For each value in "tokens" sets the specified bit in the reference table
        /// </summary>
        /// <param name="tokens"></param>
        /// <param name="bit"></param>
        private static void setBit(IEnumerable<ushort> tokens, TokenProperties bit)
        {
            foreach (var token in tokens) 
            {
                setBit(token, bit);
            }
        }
        /// <summary>
        /// Set the specified bit in the reference table for "token"
        /// </summary>
        /// <param name="value"></param>
        /// <param name="bit"></param>
        private static void setBit(ushort token, TokenProperties bit)
        {
            TokenMetadata[token] |= (ushort)bit;
        }

        #endregion
    }
}