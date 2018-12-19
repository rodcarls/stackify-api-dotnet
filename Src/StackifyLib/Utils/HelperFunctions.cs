using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StackifyLib.Internal.Serialization;


namespace StackifyLib.Utils
{
    public class HelperFunctions
    {
        private static readonly List<string> BadTypes = new List<string> { "log4net.Util.SystemStringFormat", "System.Object[]" };
        private static readonly JsonSerializer Serializer = new JsonSerializer { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, NullValueHandling = NullValueHandling.Ignore, ReferenceLoopHandling = ReferenceLoopHandling.Ignore };

        /// <summary>
        /// Trying to serialize something that the user passed in. Sometimes this is used to serialize what we know is additional debug and sometimes it is the primary logged item. This is why the serializeSimpleTypes exists. For additional debug stuff we always serialize it. For the primary logged object we won't because it doesn't make any sense to put a string in the json as well as the main message. It's meant for objects.
        /// </summary>
        /// <param name="logObject"></param>
        /// <param name="serializeSimpleTypes"></param>
        /// <param name="properties"></param>
        /// <returns></returns>
        public static string SerializeDebugData(object logObject, bool serializeSimpleTypes, Dictionary<string, object> properties = null)
        {
            Type t = null;
            JObject jObject = null;

            try
            {
                if (logObject == null)
                {
                }
                else
                {
                    t = logObject.GetType();
#if NET40
                    var typeInfo = t;
#else
                    var typeInfo = t.GetTypeInfo();
#endif
                    if (logObject is string || t.FullName == "log4net.Util.SystemStringFormat")
                    {
                        if (serializeSimpleTypes)
                        {
                            jObject = new JObject();
                            jObject.Add("logArg", new JValue(logObject.ToString()));
                        }
                    }
                    else if (typeInfo.IsPrimitive || typeInfo.BaseType == typeof(ValueType))
                    {
                        if (serializeSimpleTypes)
                        {
                            jObject = new JObject();
                            try
                            {
                                jObject.Add("logArg", new JValue(logObject));
                            }
                            catch (ArgumentException)
                            {
                                jObject.Add("logArg", new JValue(logObject.ToString()));
                            }
                        }
                    }
                    //look for some things we don't want to touch
                    else if (logObject is IDisposable)// || logObject is MarshalByRefObject)
                    {

                    }
                    else if (!BadTypes.Contains(t.ToString()))
                    {
                        var token = JToken.FromObject(logObject, Serializer);

                        if (token is JObject)
                        {
                            jObject = (JObject)token;
                            var type = logObject.GetType();

                            //do we log the objectType? Not logging it for simple things
                            if (typeInfo.IsPrimitive == false && type.Name != "String" && typeInfo.BaseType != typeof(ValueType) && type.Name.Contains("AnonymousType") == false && (type.FullName == null || type.FullName.Contains("System.Collections.Generic.Dictionary") == false))
                            {
                                jObject.Add("objectType", type.FullName);
                            }
                        }
                        else if (token is JArray)
                        {
                            jObject = new JObject();
                            jObject.Add("logArg", token);

                            var type = logObject.GetType();

                            if (type.IsArray)
                            {
                                var array = (Array)logObject;

                                if (array.Length > 0)
                                {
                                    var child = array.GetValue(0);

                                    var childtype = child.GetType();

#if NET40
                                    var childtypeinfo = childtype;
#else
                                    var childtypeinfo = childtype.GetTypeInfo();
#endif

                                    if (childtypeinfo.IsPrimitive == false && childtype.Name != "String" && childtypeinfo.BaseType != typeof(ValueType))
                                    {
                                        jObject.Add("objectType", childtype.FullName);
                                    }
                                }
                            }
                            else
                            {
                                if (!typeInfo.ContainsGenericParameters)
                                {
                                    jObject.Add("objectType", type.FullName);
                                }
                                else
                                {
#if NETFULL
                                    var genericArgs = typeInfo.GetGenericArguments();
#else
                                    var genericArgs = typeInfo.IsGenericTypeDefinition ?
                                        type.GetTypeInfo().GenericTypeParameters :
                                        type.GetTypeInfo().GenericTypeArguments;
#endif
                                    if (genericArgs != null && genericArgs.Length > 0)
                                    {
                                        var childtype = genericArgs.First();
#if NET40
                                        var childtypeinfo = childtype;
#else
                                        var childtypeinfo = childtype.GetTypeInfo();
#endif
                                        if (childtypeinfo.IsPrimitive == false && childtype.Name != "String" && childtypeinfo.BaseType != typeof(ValueType))
                                        {
                                            jObject.Add("objectType", childtype.FullName);
                                        }
                                    }
                                    else
                                    {
                                        jObject.Add("objectType", type.FullName);
                                    }
                                }
                            }
                        }
                        else if (token is JValue)
                        {
                            if (serializeSimpleTypes)
                            {
                                jObject = new JObject();
                                jObject.Add("logArg", token);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (BadTypes)
                {
                    BadTypes.Add(t.ToString());
                }
                Utils.StackifyAPILogger.Log(ex.ToString());
            }

            string data = null;
            if (properties != null && properties.Count > 0)
            {

                if (jObject == null)
                {
                    jObject = new JObject();
                }

                JObject props = new JObject();
                foreach (var prop in properties)
                {
                    try
                    {
                        if (IsValueType(prop.Value))
                        {
                            props.Add(prop.Key, new JValue(prop.Value));
                        }
                        else
                        {
                            props.Add(prop.Key, JObject.FromObject(prop.Value, Serializer));
                        }

                    }
                    catch (Exception ex)
                    {
                        StackifyAPILogger.Log(ex.ToString());
                    }

                }

                jObject.Add("context", props);

            }

            if (jObject != null)
            {
                jObject = GetPrunedObject(jObject, Config.LoggingMaxFields);

                var jdn = new JsonDotNetSerializer(SerializerSettings);

                return jdn.SafeSerializeObject(jObject);
            }

            return null;
        }

        /// <summary>
        ///     If the <see cref="JObject"/> provided has move fields than maxFields
        ///     will return a simplified <see cref="JObject"/> with original as an unparsed string message,
        ///     otherwise will return original <see cref="JObject"/>
        /// </summary>
        private static JObject GetPrunedObject(JObject obj, int maxFields)
        {
            var fieldCount = GetFieldCount(obj);

            if (fieldCount > maxFields)
            {
                return new JObject
                {
                    { "invalid", true },
                    { "message", obj.ToString() }
                };
            }

            return obj;
        }

        private static int GetFieldCount(JToken obj)
        {
            switch (obj.Type)
            {
                case JTokenType.Array:
                case JTokenType.Object:
                    return obj.Children().Sum(i => GetFieldCount(i));
                case JTokenType.Property:
                    return GetFieldCount(obj.Value<JProperty>().Value);
                default:
                    return 1;
            }
        }

        public static bool IsValueType(object obj)
        {
            if (obj == null)
                return false;

            var t = obj.GetType();
#if NET40
            return t.IsPrimitive || t.Equals(typeof(string));
#else
            return t.GetTypeInfo().IsPrimitive || t.Equals(typeof(string));
#endif
        }

        public static string CleanPartialUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.Contains("/"))
                return url;

            string[] urlPieces = url.Split(new char[] { '/' });

            var sbNewUrl = new StringBuilder(url.Length);

            int index = 0;
            foreach (string piece in urlPieces)
            {
                if (string.IsNullOrEmpty(piece))
                {
                    continue;
                }

                long val;
                Guid guidval;
                if (long.TryParse(piece, out val))
                {
                    sbNewUrl.Append("/{id}");
                }
                else if (Guid.TryParse(piece, out guidval))
                {
                    sbNewUrl.Append("/{guid}");
                }
                else
                {
                    sbNewUrl.AppendFormat("/{0}", piece);
                }

                index++;
            }

            if (url.EndsWith("/"))
            {
                sbNewUrl.Append("/");
            }

            return sbNewUrl.ToString();
        }
    }
}
