﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

namespace Trolley
{
    public class RepositoryHelper
    {
        private static Regex HasUnionRegex = new Regex(@"\bFROM\b[^()]*?(?>[^()]+|\((?<quote>)|\)(?<-quote>))*?[^()]*?(?(quote)(?!))\bUNION\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static MethodInfo ReaderGetItemByIndex = typeof(IDataRecord).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(p => p.GetIndexParameters().Length > 0 && p.GetIndexParameters()[0].ParameterType == typeof(int))
                        .Select(p => p.GetGetMethod()).First();
        private static MethodInfo StringGetItemByIndex = typeof(string).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(p => p.GetIndexParameters().Any() && p.GetIndexParameters()[0].ParameterType == typeof(int))
                        .Select(p => p.GetGetMethod()).First();
        private static ConcurrentDictionary<Type, Dictionary<int, string>> enumNameDict = new ConcurrentDictionary<Type, Dictionary<int, string>>();
        private static ConcurrentDictionary<int, Action<IDbCommand, object>> paramActionCache = new ConcurrentDictionary<int, Action<IDbCommand, object>>();
        private static ConcurrentDictionary<int, Func<DbDataReader, object>> readerCache = new ConcurrentDictionary<int, Func<DbDataReader, object>>();
        private static ConcurrentDictionary<int, string> PagingCache = new ConcurrentDictionary<int, string>();
        private static Type dictParameterType = typeof(List<KeyValuePair<string, object>>);

        internal static Action<IDbCommand, object> GetActionCache(int hashKey, string sql, IOrmProvider provider, Type paramType)
        {
            Action<IDbCommand, object> result;
            if (!paramActionCache.TryGetValue(hashKey, out result))
            {
                var mapper = new EntityMapper(Nullable.GetUnderlyingType(paramType) ?? paramType);
                var colMappers = mapper.MemberMappers.Values.Where(p => Regex.IsMatch(sql, @"[?@:]" + p.MemberName + "([^a-z0-9_]+|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant));
                result = CreateParametersHandler(provider.ParamPrefix, mapper);
                paramActionCache.TryAdd(hashKey, result);
            }
            return result;
        }
        internal static Func<DbDataReader, object> GetReader(int hashKey, Type targetType, DbDataReader reader, bool isIgnoreCase)
        {
            hashKey = RepositoryHelper.GetReaderKey(targetType, hashKey);
            if (!readerCache.ContainsKey(hashKey))
            {
                string propName = null;
                PropertyInfo propInfo = null;
                var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                if (isIgnoreCase) bindingFlags |= BindingFlags.IgnoreCase;
                List<string> propNameList = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    propName = reader.GetName(i);
                    propInfo = targetType.GetProperty(propName, bindingFlags);
                    if (propInfo == null) continue;
                    propNameList.Add(propInfo.Name);
                }
                var readerHandler = DbTypeMap.ContainsKey(targetType) ? CreateValueReaderHandler(targetType, reader.GetOrdinal(propName)) : CreateTypeReaderHandler(targetType, propNameList, reader);
                readerCache.TryAdd(hashKey, readerHandler);
            }
            return readerCache[hashKey];
        }
        internal static Action<IDbCommand, TEntity> CreateParametersHandler<TEntity>(string paramPrefix, Type entityType, IEnumerable<MemberMapper> colMappers)
        {
            var dm = new DynamicMethod("CreateParameter_" + Guid.NewGuid().ToString(), null, new[] { typeof(IDbCommand), entityType }, entityType, true);
            ILGenerator il = dm.GetILGenerator();

            il.Emit(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Callvirt, typeof(IDbCommand).GetProperty(nameof(IDbCommand.Parameters)).GetGetMethod(), null);
            // stack is now [parameters]
            foreach (var colMapper in colMappers)
            {
                CreateParamter(il, paramPrefix, colMapper, true);
            }
            // stack is now [parameters]
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            return (Action<IDbCommand, TEntity>)dm.CreateDelegate(typeof(Action<IDbCommand, TEntity>));
        }
        internal static int GetHashKey(string connString, string sqlKey, CommandType cmdType)
        {
            int hashCode = 23;
            unchecked
            {
                hashCode = hashCode * 17 + connString.GetHashCode();
                hashCode = hashCode * 17 + sqlKey.GetHashCode();
                hashCode = hashCode * 17 + cmdType.GetHashCode();
            }
            return hashCode;
        }
        internal static int GetHashKey(string connString, string sqlKey, Type paramterType)
        {
            int hashCode = 23;
            unchecked
            {
                hashCode = hashCode * 17 + connString.GetHashCode();
                hashCode = hashCode * 17 + sqlKey.GetHashCode();
                if (paramterType != null)
                {
                    hashCode = hashCode * 17 + paramterType.GetHashCode();
                }
            }
            return hashCode;
        }
        internal static int GetHashKey(int typeHashCode, string sqlKey)
        {
            int hashCode = 23;
            unchecked
            {
                hashCode = hashCode * 17 + typeHashCode;
                hashCode = hashCode * 17 + sqlKey.GetHashCode();
            }
            return hashCode;
        }
        private static Func<DbDataReader, object> CreateValueReaderHandler(Type valueType, int index)
        {
            var underlyingType = Nullable.GetUnderlyingType(valueType);
            if (underlyingType != null)
            {
                if (underlyingType.GetTypeInfo().IsEnum)
                {
                    return f =>
                    {
                        var objValue = f.GetValue(index);
                        if (objValue is DBNull) return null;
                        if (f.GetFieldType(index) == typeof(string)) return Enum.Parse(underlyingType, objValue.ToString(), true);
                        else return Enum.ToObject(underlyingType, objValue);
                    };
                }
                else
                {
                    return f =>
                    {
                        var objValue = f.GetValue(index);
                        return objValue is DBNull ? null : objValue;
                    };
                }
            }
            else return f => { return f.GetValue(index); };
        }
        private static Func<DbDataReader, object> CreateTypeReaderHandler(Type entityType, List<string> propNameList, DbDataReader reader)
        {
            var isValueType = entityType.GetTypeInfo().IsValueType;
            var returnType = isValueType ? typeof(object) : entityType;
            var dm = new DynamicMethod("CreateTReader_" + Guid.NewGuid().ToString(), returnType, new[] { typeof(DbDataReader) }, entityType, true);
            var il = dm.GetILGenerator();
            il.DeclareLocal(typeof(int));
            il.DeclareLocal(entityType);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc_0);

            var supportInitialize = false;
            if (isValueType)
            {
                il.Emit(OpCodes.Ldloca_S, (byte)1);
                il.Emit(OpCodes.Initobj, entityType);
            }
            else
            {
                if (entityType.GetConstructor(Type.EmptyTypes) == null)
                    throw new Exception(String.Format("类型{0}必须提供无类型参数的构造方法", entityType.FullName));
                il.Emit(OpCodes.Newobj, entityType.GetConstructor(Type.EmptyTypes));
                il.Emit(OpCodes.Stloc_1);

                supportInitialize = typeof(ISupportInitialize).IsAssignableFrom(entityType);
                if (supportInitialize)
                {
                    il.Emit(OpCodes.Ldloc_1);
                    il.EmitCall(OpCodes.Callvirt, typeof(ISupportInitialize).GetMethod(nameof(ISupportInitialize.BeginInit)), null);
                }
            }

            il.BeginExceptionBlock();

            if (isValueType) il.Emit(OpCodes.Ldloca_S, (byte)1);// [target] 
            else il.Emit(OpCodes.Ldloc_1);// [target]            

            var allDone = il.DefineLabel();
            int valueCopyLocal = il.DeclareLocal(typeof(object)).LocalIndex;
            int toTypeLocal = il.DeclareLocal(typeof(string)).LocalIndex;

            foreach (var propName in propNameList)
            {
                // stack is now [target][target]
                il.Emit(OpCodes.Dup);
                // stack is now [target][target][reader][index]
                il.Emit(OpCodes.Ldarg_0);
                LoadInt32(il, reader.GetOrdinal(propName));
                il.Emit(OpCodes.Dup);
                StoreLocal(il, toTypeLocal);

                il.EmitCall(OpCodes.Callvirt, ReaderGetItemByIndex, null);
                // stack is now [target][target][value]

                il.Emit(OpCodes.Dup); // stack is now [target][target][value-as-object][value-as-object]
                StoreLocal(il, valueCopyLocal);

                // stack is now [target][target][value][value]
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Isinst, typeof(DBNull));
                // stack is now [target][target][value][DBNull or null]
                Label dbNullLabel = il.DefineLabel();
                il.Emit(OpCodes.Brtrue_S, dbNullLabel);

                var colType = reader.GetFieldType(reader.GetOrdinal(propName));
                var propType = entityType.GetProperty(propName).PropertyType;
                var underlyingType = Nullable.GetUnderlyingType(propType);
                bool isNullable = underlyingType != null;
                if (!isNullable) underlyingType = propType;

                il.Emit(OpCodes.Ldstr, colType.FullName);
                StoreLocal(il, toTypeLocal);

                if (underlyingType.FullName == DbTypeMap.LinqBinary)
                {
                    il.Emit(OpCodes.Unbox_Any, typeof(byte[]));
                    il.Emit(OpCodes.Newobj, propType.GetConstructor(new Type[] { typeof(byte[]) }));
                    // stack is now [target][target][byte[]-value]
                }
                else if (underlyingType == colType) il.Emit(OpCodes.Unbox_Any, propType);
                else
                {
                    if (underlyingType == typeof(Guid))
                    {
                        il.Emit(OpCodes.Unbox_Any, colType);
                        //支持byte[],string类型
                        il.Emit(OpCodes.Newobj, typeof(Guid).GetConstructor(new Type[] { colType }));
                    }
                    else if (underlyingType == typeof(char))
                    {
                        if (colType == typeof(string))
                        {
                            il.Emit(OpCodes.Unbox_Any, colType);
                            LoadInt32(il, 0);
                            il.EmitCall(OpCodes.Call, StringGetItemByIndex, null);
                        }
                    }
                    else if (underlyingType == typeof(bool))
                    {
                        il.Emit(OpCodes.Unbox_Any, colType);
                        if (colType != typeof(int)) il.Emit(OpCodes.Conv_I4);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ceq);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ceq);
                    }
                    else
                    {
                        var propTypeCode = Type.GetTypeCode(underlyingType);
                        var colTypeCode = Type.GetTypeCode(colType);
                        if (underlyingType.GetTypeInfo().IsEnum)
                        {
                            propTypeCode = Type.GetTypeCode(Enum.GetUnderlyingType(underlyingType));
                            if (propTypeCode != colTypeCode)
                            {
                                if (colType == typeof(string))
                                {
                                    il.Emit(OpCodes.Castclass, typeof(string));
                                    var localIndex = il.DeclareLocal(colType).LocalIndex;
                                    il.Emit(OpCodes.Stloc_S, localIndex);
                                    il.Emit(OpCodes.Ldtoken, underlyingType);
                                    il.EmitCall(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)), null);
                                    il.Emit(OpCodes.Ldloc_S, localIndex);
                                    il.Emit(OpCodes.Ldc_I4_1);
                                    il.EmitCall(OpCodes.Call, typeof(Enum).GetMethod(nameof(Enum.Parse), new Type[] { typeof(Type), typeof(string), typeof(bool) }), null);
                                    il.Emit(OpCodes.Unbox_Any, underlyingType);
                                }
                                else
                                {
                                    il.Emit(OpCodes.Unbox_Any, colType);
                                    ConvertToNumber(il, colType, propTypeCode, colTypeCode);
                                }
                            }
                            else il.Emit(OpCodes.Unbox_Any, underlyingType);
                        }
                        else
                        {
                            il.Emit(OpCodes.Unbox_Any, colType);
                            if (!ConvertToNumber(il, colType, propTypeCode, colTypeCode))
                                ConvertTo(il, colType, underlyingType);
                        }
                    }
                    if (isNullable) il.Emit(OpCodes.Newobj, propType.GetConstructor(new[] { underlyingType }));
                }

                // stack is now [target][target][typed-value]
                il.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, entityType.GetProperty(propName).GetSetMethod());

                var endLabel = il.DefineLabel();
                il.Emit(OpCodes.Br_S, endLabel);

                il.MarkLabel(dbNullLabel);
                // stack is now [target][target][value]
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
                il.MarkLabel(endLabel);
            }

            if (isValueType) il.Emit(OpCodes.Pop);
            else
            {
                il.Emit(OpCodes.Stloc_1); // stack is empty

                if (supportInitialize)
                {
                    il.Emit(OpCodes.Ldloc_1);
                    il.EmitCall(OpCodes.Callvirt, typeof(ISupportInitialize).GetMethod(nameof(ISupportInitialize.EndInit)), null);
                }
            }

            il.MarkLabel(allDone);
            il.BeginCatchBlock(typeof(Exception)); // stack is Exception
            il.Emit(OpCodes.Ldloc_0); // stack is Exception, index
            il.Emit(OpCodes.Ldarg_0); // stack is Exception, index, reader
            LoadLocal(il, toTypeLocal);
            LoadLocal(il, valueCopyLocal); // stack is Exception, index, reader, value
            il.EmitCall(OpCodes.Call, typeof(RepositoryHelper).GetMethod(nameof(RepositoryHelper.ThrowDataException)), null);
            il.EndExceptionBlock();

            il.Emit(OpCodes.Ldloc_1); // stack is [rval]
            if (isValueType) il.Emit(OpCodes.Box, entityType);
            il.Emit(OpCodes.Ret);

            return (Func<DbDataReader, object>)dm.CreateDelegate(typeof(Func<DbDataReader, object>));
        }
        private static Action<IDbCommand, object> CreateParametersHandler(string paramPrefix, EntityMapper mapper)
        {
            var dm = new DynamicMethod("CreateParameter_" + Guid.NewGuid().ToString(), null, new[] { typeof(IDbCommand), typeof(object) }, mapper.EntityType, true);
            ILGenerator il = dm.GetILGenerator();

            il.Emit(OpCodes.Nop);
            il.DeclareLocal(mapper.EntityType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, mapper.EntityType);
            il.Emit(OpCodes.Stloc_0);

            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Callvirt, typeof(IDbCommand).GetProperty(nameof(IDbCommand.Parameters)).GetGetMethod(), null);
            foreach (var colMapper in mapper.MemberMappers.Values)
            {
                CreateParamter(il, paramPrefix, colMapper, false);
            }
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            return (Action<IDbCommand, object>)dm.CreateDelegate(typeof(Action<IDbCommand, object>));
        }
        private static Action<IDbCommand, object> CreateParametersHandler(string paramPrefix, Dictionary<string, object> mapper)
        {
            var dm = new DynamicMethod("CreateParameter_" + Guid.NewGuid().ToString(), null, new[] { typeof(IDbCommand), typeof(object) }, dictParameterType, true);
            ILGenerator il = dm.GetILGenerator();

            il.Emit(OpCodes.Nop);
            il.DeclareLocal(typeof(Dictionary<string, object>));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, typeof(Dictionary<string, object>));
            il.Emit(OpCodes.Stloc_0);

            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Callvirt, typeof(IDbCommand).GetProperty(nameof(IDbCommand.Parameters)).GetGetMethod(), null);
            foreach (var colMapper in mapper)
            {
                CreateParamter(il, paramPrefix, colMapper);
            }
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            return (Action<IDbCommand, object>)dm.CreateDelegate(typeof(Action<IDbCommand, object>));
        }
        private static void CreateParamter(ILGenerator il, string paramPrefix, MemberMapper colMapper, bool isTyped)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Callvirt, typeof(IDbCommand).GetMethod(nameof(IDbCommand.CreateParameter), Type.EmptyTypes), null);

            // stack is now [parameters][parameters][parameter]
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldstr, paramPrefix + colMapper.MemberName);
            il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.ParameterName)).GetSetMethod(), null);
            // stack is now [parameters][parameters][parameter]

            // stack is now [parameters][parameters][parameter][parameter].DbType={DbType}
            il.Emit(OpCodes.Dup);
            LoadInt32(il, (int)colMapper.DbType);
            il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.DbType)).GetSetMethod(), null);

            // stack is now [parameters][parameters][parameter][parameter].Direction={ParameterDirection.Input}
            il.Emit(OpCodes.Dup);
            LoadInt32(il, (int)ParameterDirection.Input);
            il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Direction)).GetSetMethod(), null);

            il.Emit(OpCodes.Dup);
            if (isTyped) il.Emit(OpCodes.Ldarg_1);
            else il.Emit(OpCodes.Ldloc_0);

            il.Emit(OpCodes.Callvirt, colMapper.GetMethodInfo);
            // stack is now [parameters][parameters][parameter][parameter][value]
            if (colMapper.IsValueType)
            {
                il.Emit(OpCodes.Box, colMapper.MemberType);
                if (colMapper.IsEnum && colMapper.IsString)
                {
                    // stack is now [parameters][parameters][parameter][parameter][int-value][enum-type]
                    il.Emit(OpCodes.Ldtoken, colMapper.UnderlyingType);
                    il.EmitCall(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)), null);
                    // stack is now [parameters][parameters][parameter][parameter][int-value][enum-type]
                    il.EmitCall(OpCodes.Call, typeof(RepositoryHelper).GetMethod(nameof(RepositoryHelper.GetEnumName), BindingFlags.Static | BindingFlags.Public), null);
                    il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);

                    //直接设置长度为100，枚举值都不会超过100个字符，避免了做类似下面的复杂逻辑操作
                    il.Emit(OpCodes.Dup);
                    LoadInt32(il, 100);
                    il.EmitCall(OpCodes.Callvirt, typeof(IDbDataParameter).GetProperty(nameof(IDbDataParameter.Size)).GetSetMethod(), null);

                    #region 获取字符串长度，设置长度
                    //// stack is now [parameters][parameters][parameter][parameter][string or dbNull]
                    //il.Emit(OpCodes.Dup);
                    //il.Emit(OpCodes.Isinst, typeof(DBNull));
                    //// stack is now [target][target][value][DBNull or null]
                    //Label dbNullLabel = il.DefineLabel();
                    //il.Emit(OpCodes.Brtrue_S, dbNullLabel);

                    ////非空字符串
                    //var iLocIndex = il.DeclareLocal(typeof(string)).LocalIndex;
                    //il.Emit(OpCodes.Dup);
                    //il.Emit(OpCodes.Castclass, typeof(string));
                    //il.Emit(OpCodes.Stloc, iLocIndex);
                    //il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);
                    //// stack is now [parameters][parameters][parameter]

                    ////设置长度
                    //il.Emit(OpCodes.Dup);
                    //il.Emit(OpCodes.Ldloc, iLocIndex);
                    //il.EmitCall(OpCodes.Callvirt, typeof(string).GetProperty(nameof(string.Length)).GetGetMethod(), null);
                    //// stack is now [parameters][parameters][parameter][string-length]
                    //il.EmitCall(OpCodes.Callvirt, typeof(IDbDataParameter).GetProperty(nameof(IDbDataParameter.Size)).GetSetMethod(), null);

                    //Label allDoneLabel = il.DefineLabel();
                    //il.Emit(OpCodes.Br_S, allDoneLabel);

                    //il.MarkLabel(dbNullLabel);
                    //il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);
                    //il.MarkLabel(allDoneLabel);
                    #endregion
                }
                else
                {
                    if (colMapper.IsNullable)
                    {
                        il.Emit(OpCodes.Dup);
                        Label notNullLabel = il.DefineLabel();
                        il.Emit(OpCodes.Brtrue_S, notNullLabel);
                        // stack is now [parameters][parameters][parameter]
                        il.Emit(OpCodes.Pop);
                        il.Emit(OpCodes.Ldsfld, typeof(DBNull).GetField(nameof(DBNull.Value)));
                        il.MarkLabel(notNullLabel);
                    }
                    // stack is now [parameters][parameters][parameter][parameter][object-value]
                    il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);
                }
            }
            else
            {
                // stack is now [parameters][parameters][parameter] 
                il.Emit(OpCodes.Dup);
                Label notNullLabel = il.DefineLabel();
                il.Emit(OpCodes.Brtrue_S, notNullLabel);
                // stack is now [parameters][parameters][parameter][parameter]
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldsfld, typeof(DBNull).GetField(nameof(DBNull.Value)));
                // stack is now [parameters][parameters][parameter][parameter][DBNull]
                il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);

                Label allDoneLabel = il.DefineLabel();
                il.Emit(OpCodes.Br_S, allDoneLabel);

                //不为空
                il.MarkLabel(notNullLabel);
                int index = 0;
                if (colMapper.IsString)
                {
                    il.Emit(OpCodes.Dup);
                    // stack is now [parameters][parameters][parameter][parameter][string][string]
                    il.EmitCall(OpCodes.Callvirt, typeof(string).GetProperty(nameof(string.Length)).GetGetMethod(), null);
                    LoadInt32(il, 4000);
                    il.Emit(OpCodes.Cgt); // [string] [0 or 1]
                    Label isLong = il.DefineLabel(), lenDone = il.DefineLabel();
                    il.Emit(OpCodes.Brtrue_S, isLong);
                    LoadInt32(il, 4000); // [string] [4000]
                    il.Emit(OpCodes.Br_S, lenDone);
                    il.MarkLabel(isLong);
                    LoadInt32(il, -1); // [string] [-1]
                    il.MarkLabel(lenDone);

                    index = il.DeclareLocal(typeof(int)).LocalIndex;
                    il.Emit(OpCodes.Stloc_S, index);
                }
                if (colMapper.IsLinqBinary)
                {
                    il.EmitCall(OpCodes.Callvirt, colMapper.MemberType.GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance), null);
                    // stack is now [parameters][parameters][parameter][parameter][bytesArray]
                }
                il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);

                // stack is now [parameters][parameters][parameter]
                if (colMapper.IsString)
                {
                    il.Emit(OpCodes.Dup);
                    // stack is now [parameters][parameters][parameter][parameter].Size=len(string)
                    il.Emit(OpCodes.Ldloc_S, (byte)index);
                    il.EmitCall(OpCodes.Callvirt, typeof(IDbDataParameter).GetProperty(nameof(IDbDataParameter.Size)).GetSetMethod(), null);
                    // stack is now [parameters][parameters][parameter]                        
                }
                il.MarkLabel(allDoneLabel);
            }
            // stack is now [parameters][parameters].Add([parameter])
            il.EmitCall(OpCodes.Callvirt, typeof(IList).GetMethod(nameof(IList.Add)), null);
            il.Emit(OpCodes.Pop);
            // stack is now [parameters]
        }
        private static void CreateParamter(ILGenerator il, string paramPrefix, KeyValuePair<string, object> colMapper)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Callvirt, typeof(IDbCommand).GetMethod(nameof(IDbCommand.CreateParameter), Type.EmptyTypes), null);

            // stack is now [parameters][parameters][parameter]
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldstr, paramPrefix + colMapper.Key);
            il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.ParameterName)).GetSetMethod(), null);
            // stack is now [parameters][parameters][parameter]

            var memberType = colMapper.Value.GetType();
            var dbType = DbTypeMap.LookupDbType(colMapper.Value.GetType());
            var underlyingType = memberType;
            var isValueType = memberType.GetTypeInfo().IsValueType;
            var isEnum = memberType.GetTypeInfo().IsEnum;
            var isNullable = Nullable.GetUnderlyingType(memberType) != null;
            if (isNullable)
            {
                underlyingType = Nullable.GetUnderlyingType(memberType);
                isEnum = underlyingType.GetTypeInfo().IsEnum;
            }
            var isString = underlyingType == typeof(string);

            // stack is now [parameters][parameters][parameter][parameter].DbType={DbType}
            il.Emit(OpCodes.Dup);
            LoadInt32(il, (int)dbType);
            il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.DbType)).GetSetMethod(), null);

            // stack is now [parameters][parameters][parameter][parameter].Direction={ParameterDirection.Input}
            il.Emit(OpCodes.Dup);
            LoadInt32(il, (int)ParameterDirection.Input);
            il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Direction)).GetSetMethod(), null);

            //byte,double,float,int,long,sbyte,short,string,Type
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldstr, colMapper.Key);
            il.Emit(OpCodes.Call, typeof(KeyValuePair<string, object>).GetProperty(nameof(KeyValuePair<string, object>.Value)).GetGetMethod());

            // stack is now [parameters][parameters][parameter][parameter][value]
            if (memberType.GetTypeInfo().IsValueType)
            {
                il.Emit(OpCodes.Box, memberType);
                if (memberType.GetTypeInfo().IsEnum && memberType == typeof(string))
                {
                    // stack is now [parameters][parameters][parameter][parameter][int-value][enum-type]
                    il.Emit(OpCodes.Ldtoken, underlyingType);
                    il.EmitCall(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)), null);
                    // stack is now [parameters][parameters][parameter][parameter][int-value][enum-type]
                    il.EmitCall(OpCodes.Call, typeof(RepositoryHelper).GetMethod(nameof(RepositoryHelper.GetEnumName), BindingFlags.Static | BindingFlags.Public), null);
                    il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);

                    //直接设置长度为100，枚举值都不会超过100个字符，避免了做类似下面的复杂逻辑操作
                    il.Emit(OpCodes.Dup);
                    LoadInt32(il, 100);
                    il.EmitCall(OpCodes.Callvirt, typeof(IDbDataParameter).GetProperty(nameof(IDbDataParameter.Size)).GetSetMethod(), null);

                    #region 获取字符串长度，设置长度
                    //// stack is now [parameters][parameters][parameter][parameter][string or dbNull]
                    //il.Emit(OpCodes.Dup);
                    //il.Emit(OpCodes.Isinst, typeof(DBNull));
                    //// stack is now [target][target][value][DBNull or null]
                    //Label dbNullLabel = il.DefineLabel();
                    //il.Emit(OpCodes.Brtrue_S, dbNullLabel);

                    ////非空字符串
                    //var iLocIndex = il.DeclareLocal(typeof(string)).LocalIndex;
                    //il.Emit(OpCodes.Dup);
                    //il.Emit(OpCodes.Castclass, typeof(string));
                    //il.Emit(OpCodes.Stloc, iLocIndex);
                    //il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);
                    //// stack is now [parameters][parameters][parameter]

                    ////设置长度
                    //il.Emit(OpCodes.Dup);
                    //il.Emit(OpCodes.Ldloc, iLocIndex);
                    //il.EmitCall(OpCodes.Callvirt, typeof(string).GetProperty(nameof(string.Length)).GetGetMethod(), null);
                    //// stack is now [parameters][parameters][parameter][string-length]
                    //il.EmitCall(OpCodes.Callvirt, typeof(IDbDataParameter).GetProperty(nameof(IDbDataParameter.Size)).GetSetMethod(), null);

                    //Label allDoneLabel = il.DefineLabel();
                    //il.Emit(OpCodes.Br_S, allDoneLabel);

                    //il.MarkLabel(dbNullLabel);
                    //il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);
                    //il.MarkLabel(allDoneLabel);
                    #endregion
                }
                else
                {
                    if (isNullable)
                    {
                        il.Emit(OpCodes.Dup);
                        Label notNullLabel = il.DefineLabel();
                        il.Emit(OpCodes.Brtrue_S, notNullLabel);
                        // stack is now [parameters][parameters][parameter]
                        il.Emit(OpCodes.Pop);
                        il.Emit(OpCodes.Ldsfld, typeof(DBNull).GetField(nameof(DBNull.Value)));
                        il.MarkLabel(notNullLabel);
                    }
                    // stack is now [parameters][parameters][parameter][parameter][object-value]
                    il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);
                }
            }
            else
            {
                // stack is now [parameters][parameters][parameter] 
                il.Emit(OpCodes.Dup);
                Label notNullLabel = il.DefineLabel();
                il.Emit(OpCodes.Brtrue_S, notNullLabel);
                // stack is now [parameters][parameters][parameter][parameter]
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldsfld, typeof(DBNull).GetField(nameof(DBNull.Value)));
                // stack is now [parameters][parameters][parameter][parameter][DBNull]
                il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);

                Label allDoneLabel = il.DefineLabel();
                il.Emit(OpCodes.Br_S, allDoneLabel);

                //不为空
                il.MarkLabel(notNullLabel);
                int? stringIndex = null;
                if (isString)
                {
                    il.Emit(OpCodes.Dup);
                    // stack is now [parameters][parameters][parameter][parameter][string][string]
                    il.EmitCall(OpCodes.Callvirt, typeof(string).GetProperty(nameof(string.Length)).GetGetMethod(), null);
                    LoadInt32(il, 4000);
                    il.Emit(OpCodes.Cgt); // [string] [0 or 1]
                    Label isLong = il.DefineLabel(), lenDone = il.DefineLabel();
                    il.Emit(OpCodes.Brtrue_S, isLong);
                    LoadInt32(il, 4000); // [string] [4000]
                    il.Emit(OpCodes.Br_S, lenDone);
                    il.MarkLabel(isLong);
                    LoadInt32(il, -1); // [string] [-1]
                    il.MarkLabel(lenDone);

                    stringIndex = il.DeclareLocal(typeof(int)).LocalIndex;
                    il.Emit(OpCodes.Stloc_S, (byte)stringIndex);
                }
                il.EmitCall(OpCodes.Callvirt, typeof(IDataParameter).GetProperty(nameof(IDataParameter.Value)).GetSetMethod(), null);

                // stack is now [parameters][parameters][parameter]
                if (isString)
                {
                    il.Emit(OpCodes.Dup);
                    // stack is now [parameters][parameters][parameter][parameter].Size=len(string)
                    il.Emit(OpCodes.Stloc_S, (byte)stringIndex);
                    il.EmitCall(OpCodes.Callvirt, typeof(IDbDataParameter).GetProperty(nameof(IDbDataParameter.Size)).GetSetMethod(), null);
                    // stack is now [parameters][parameters][parameter]                        
                }
                il.MarkLabel(allDoneLabel);
            }
            // stack is now [parameters][parameters].Add([parameter])
            il.EmitCall(OpCodes.Callvirt, typeof(IList).GetMethod(nameof(IList.Add)), null);
            il.Emit(OpCodes.Pop);
            // stack is now [parameters]
        }
        private static bool ConvertToNumber(ILGenerator il, Type colType, TypeCode propTypeCode, TypeCode colTypeCode)
        {
            OpCode opCode = default(OpCode);
            switch (colTypeCode)
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                    switch (propTypeCode)
                    {
                        case TypeCode.Byte:
                            opCode = OpCodes.Conv_Ovf_I1_Un; break;
                        case TypeCode.SByte:
                            opCode = OpCodes.Conv_Ovf_I1; break;
                        case TypeCode.UInt16:
                            opCode = OpCodes.Conv_Ovf_I2_Un; break;
                        case TypeCode.Int16:
                            opCode = OpCodes.Conv_Ovf_I2; break;
                        case TypeCode.UInt32:
                            opCode = OpCodes.Conv_Ovf_I4_Un; break;
                        case TypeCode.Boolean:
                        case TypeCode.Int32:
                            opCode = OpCodes.Conv_Ovf_I4; break;
                        case TypeCode.UInt64:
                            opCode = OpCodes.Conv_Ovf_I8_Un; break;
                        case TypeCode.Int64:
                            opCode = OpCodes.Conv_Ovf_I8; break;
                        case TypeCode.Single:
                            opCode = OpCodes.Conv_R4; break;
                        case TypeCode.Double:
                            opCode = OpCodes.Conv_R8; break;
                    }
                    break;
                default: return false;
            }
            il.Emit(opCode);
            return true;
        }
        private static void ConvertTo(ILGenerator il, Type colType, Type underlyingType)
        {
            MethodInfo op = null;
            if ((op = GetOperator(colType, underlyingType)) != null)
            {
                il.Emit(OpCodes.Call, op);
            }
            else
            {
                il.Emit(OpCodes.Ldtoken, underlyingType);
                il.EmitCall(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)), null);
                il.EmitCall(OpCodes.Call, typeof(Convert).GetMethod(nameof(Convert.ChangeType), new Type[] { typeof(object), typeof(Type) }), null);
                il.Emit(OpCodes.Unbox_Any, underlyingType);
            }
        }
        private static MethodInfo GetOperator(Type fromType, Type toType)
        {
            if (toType == null) return null;
            MethodInfo[] fromMethods, toMethods;
            return ResolveOperator(fromMethods = fromType.GetMethods(BindingFlags.Static | BindingFlags.Public), fromType, toType, "op_Implicit")
                ?? ResolveOperator(toMethods = toType.GetMethods(BindingFlags.Static | BindingFlags.Public), fromType, toType, "op_Implicit")
                ?? ResolveOperator(fromMethods, fromType, toType, "op_Explicit")
                ?? ResolveOperator(toMethods, fromType, toType, "op_Explicit");

        }
        private static MethodInfo ResolveOperator(MethodInfo[] methods, Type from, Type to, string name)
        {
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != name || methods[i].ReturnType != to) continue;
                var args = methods[i].GetParameters();
                if (args.Length != 1 || args[0].ParameterType != from) continue;
                return methods[i];
            }
            return null;
        }
        internal static int GetReaderKey(Type type, int hashKey)
        {
            unchecked
            {
                int hashCode = 23;
                hashCode = hashCode * 17 + type.GetHashCode();
                hashCode = hashCode * 17 + hashKey;
                return hashCode;
            }
        }
        private static void StoreLocal(ILGenerator il, int index)
        {
            if (index < 0 || index >= short.MaxValue) throw new ArgumentNullException(nameof(index));
            switch (index)
            {
                case 0: il.Emit(OpCodes.Stloc_0); break;
                case 1: il.Emit(OpCodes.Stloc_1); break;
                case 2: il.Emit(OpCodes.Stloc_2); break;
                case 3: il.Emit(OpCodes.Stloc_3); break;
                default:
                    if (index <= 255)
                    {
                        il.Emit(OpCodes.Stloc_S, (byte)index);
                    }
                    else
                    {
                        il.Emit(OpCodes.Stloc, (short)index);
                    }
                    break;
            }
        }
        [Obsolete("This method is for internal use only", false)]
        public static void ThrowDataException(Exception ex, int index, IDataReader reader, string toType, object value)
        {
            Exception toThrow;
            try
            {
                var colName = reader.GetName(index);
                var formattedValue = String.Empty;
                var fromType = String.Empty;
                colName = reader.GetName(index);
                try
                {
                    if (value == null || value is DBNull)
                    {
                        fromType = "null/DBNull";
                        formattedValue = "<null>";
                    }
                    else
                    {
                        formattedValue = Convert.ToString(value);
                        fromType = value.GetType().FullName;
                    }
                }
                catch (Exception valEx) { formattedValue = valEx.Message; }
                toThrow = new DataException($"反序列化失败，Reader[{index}-{colName}](Value:{formattedValue},Type:{fromType}) to Type:{toType} fail", ex);
            }
            catch { toThrow = new DataException(ex.Message, ex); }
            throw toThrow;
        }
        private static void LoadInt32(ILGenerator il, int value)
        {
            switch (value)
            {
                case -1: il.Emit(OpCodes.Ldc_I4_M1); break;
                case 0: il.Emit(OpCodes.Ldc_I4_0); break;
                case 1: il.Emit(OpCodes.Ldc_I4_1); break;
                case 2: il.Emit(OpCodes.Ldc_I4_2); break;
                case 3: il.Emit(OpCodes.Ldc_I4_3); break;
                case 4: il.Emit(OpCodes.Ldc_I4_4); break;
                case 5: il.Emit(OpCodes.Ldc_I4_5); break;
                case 6: il.Emit(OpCodes.Ldc_I4_6); break;
                case 7: il.Emit(OpCodes.Ldc_I4_7); break;
                case 8: il.Emit(OpCodes.Ldc_I4_8); break;
                default:
                    if (value >= -128 && value <= 127)
                    {
                        il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4, value);
                    }
                    break;
            }
        }
        private static void LoadLocal(ILGenerator il, int index)
        {
            if (index < 0 || index >= short.MaxValue) throw new ArgumentNullException(nameof(index));
            switch (index)
            {
                case 0: il.Emit(OpCodes.Ldloc_0); break;
                case 1: il.Emit(OpCodes.Ldloc_1); break;
                case 2: il.Emit(OpCodes.Ldloc_2); break;
                case 3: il.Emit(OpCodes.Ldloc_3); break;
                default:
                    if (index <= 255)
                    {
                        il.Emit(OpCodes.Ldloc_S, (byte)index);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldloc, (short)index);
                    }
                    break;
            }
        }
        public static object GetEnumName(object enumValue, Type enumType)
        {
            if (!enumNameDict.ContainsKey(enumType))
            {
                Dictionary<int, string> enumDict = new Dictionary<int, string>();
                var valueList = Enum.GetValues(enumType);
                foreach (var value in valueList)
                {
                    enumDict.Add(Convert.ToInt32(value), Enum.GetName(enumType, value));
                }
                enumNameDict.TryAdd(enumType, enumDict);
            }
            if (enumValue == null) return DBNull.Value;
            int iEnumValue = Convert.ToInt32(enumValue);
            if (!enumNameDict[enumType].ContainsKey(iEnumValue)) return DBNull.Value;
            return enumNameDict[enumType][iEnumValue];
        }
    }
}
