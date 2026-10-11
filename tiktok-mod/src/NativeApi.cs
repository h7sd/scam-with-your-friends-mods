using System;
using System.Reflection;
using HarmonyLib;

namespace ScamWYF.TikTokPocket
{
    internal static class NativeApi
    {
        internal static Type Type(string name)
        {
            var type=AccessTools.TypeByName("Kolkata.UI."+name);
            if(type==null) throw new InvalidOperationException("The game app interface is unavailable.");
            return type;
        }
        internal static FieldInfo Field(Type type,string name)
        {
            var field=AccessTools.Field(type,name);
            if(field==null) throw new InvalidOperationException("The game app interface changed.");
            return field;
        }
        internal static object Get(object target,string name) {return target==null?null:Field(target.GetType(),name).GetValue(target);}
        internal static void Set(object target,string name,object value) {Field(target.GetType(),name).SetValue(target,value);}
        internal static object New(string name) {return Activator.CreateInstance(Type(name),true);}
        internal static bool Own(object app) {return (string)Get(Get(app,"Definition"),"id")==Plugin.AppId;}
    }
}
