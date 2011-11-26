#region -- License Terms --
//
// MessagePack for CLI
//
// Copyright (C) 2010 FUJIWARA, Yusuke
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
#endregion -- License Terms --

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using NLiblet.Reflection;
using System.Collections.Generic;
using System.Text;
using System.Linq.Expressions;

namespace MsgPack.Serialization
{
	/*
	 *	�ڐA�@�F
	 *	�쐬���\�b�h��Emitter ��n��
	 *		Dynamicxxx�͂��悤�Ȃ�
	 *		���������ׂ� 1 ���炷
	 *		�߂�l�� void ��
	 *	�R���X�g���N�^�� Emitter.CreateInstance( context ) �łƂ��V���A���C�U��ۑ�
	 *	��L��Ώۂ̃v���p�e�B���J��Ԃ�
	 *	�e�X�g�e�X�g�e�X�g
	 */

	/// <summary>
	///		Genereates serialization methods which can be save to file.
	/// </summary>
	internal sealed class SerializerEmitter
	{
		private static readonly MethodInfo _serializationContextGet1Method = typeof( SerializationContext ).GetMethod( "Get" );
		private static readonly Type[] _constructorParameterTypes = new[] { typeof( SerializationContext ) };
		private static readonly Type[] _unpackFromCoreParameterTypes = new[] { typeof( Unpacker ) };

		/// <summary>
		///		 Gets a value indicating whether this instance is trace enabled.
		/// </summary>
		/// <value>
		/// 	<c>true</c> if the trace enabled; otherwise, <c>false</c>.
		/// </value>
		private static bool IsTraceEnabled
		{
			get { return ( Tracer.Emit.Switch.Level & SourceLevels.Verbose ) == SourceLevels.Verbose; }
		}

		private readonly TextWriter _trace = IsTraceEnabled ? new StringWriter() : TextWriter.Null;

		/// <summary>
		///		Gets the <see cref="TextWriter"/> for tracing.
		/// </summary>
		/// <value>
		///		The <see cref="TextWriter"/> for tracing.
		///		This value will not be <c>null</c>.
		/// </value>
		private TextWriter Trace { get { return this._trace; } }

		/// <summary>
		///		Flushes the trace.
		/// </summary>
		public void FlushTrace()
		{
			StringWriter writer = this._trace as StringWriter;
			if ( writer != null )
			{
				var buffer = writer.GetStringBuilder();
				var source = Tracer.Emit;
				if ( source != null && 0 < buffer.Length )
				{
					source.TraceData( Tracer.EventType.ILTrace, Tracer.EventId.ILTrace, buffer );
				}

				buffer.Clear();
			}
		}

		private readonly ConstructorBuilder _constructorBuilder;
		private readonly TypeBuilder _typeBuilder;
		private readonly MethodBuilder _packMethodBuilder;
		private readonly MethodBuilder _unpackFromMethodBuilder;
		private MethodBuilder _unpackToMethodBuilder;
		private readonly Dictionary<RuntimeTypeHandle, FieldBuilder> _serializers;
		private readonly bool _isDebuggable;

		public SerializerEmitter( ModuleBuilder host, int sequence, Type targetType, bool isDebuggable )
		{
			string typeName =
				String.Join(
				Type.Delimiter.ToString(),
				typeof( SerializerEmitter ).Namespace,
				"Generated",
				IdentifierUtility.EscapeTypeName( targetType ) + "Serializer" + sequence
			);
			Tracer.Emit.TraceEvent( Tracer.EventType.DefineType, Tracer.EventId.DefineType, "Create {0}", typeName );
			this._typeBuilder =
				host.DefineType(
					typeName,
					TypeAttributes.Sealed | TypeAttributes.Public | TypeAttributes.AutoClass | TypeAttributes.AutoLayout | TypeAttributes.BeforeFieldInit,
					typeof( MessagePackSerializer<> ).MakeGenericType( targetType )
				);

			this._constructorBuilder = this._typeBuilder.DefineConstructor( MethodAttributes.Public, CallingConventions.Standard, _constructorParameterTypes );

			this._packMethodBuilder =
				this._typeBuilder.DefineMethod(
					"PackToCore",
					MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.Final,
					CallingConventions.HasThis,
					typeof( void ),
					new Type[] { typeof( Packer ), targetType }
				);

			this._unpackFromMethodBuilder =
				this._typeBuilder.DefineMethod(
					"UnpackFromCore",
					MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.Final,
					CallingConventions.HasThis,
					targetType,
					_unpackFromCoreParameterTypes
				);

			this._typeBuilder.DefineMethodOverride( this._packMethodBuilder, this._typeBuilder.BaseType.GetMethod( this._packMethodBuilder.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic ) );
			this._typeBuilder.DefineMethodOverride( this._unpackFromMethodBuilder, this._typeBuilder.BaseType.GetMethod( this._unpackFromMethodBuilder.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic ) );

			this._serializers = new Dictionary<RuntimeTypeHandle, FieldBuilder>();
			this._isDebuggable = isDebuggable;
		}

		public TracingILGenerator GetPackToMethodILGenerator()
		{
			if ( IsTraceEnabled )
			{
				this.Trace.WriteLine( "{0}::{1}", MethodBase.GetCurrentMethod(), this._packMethodBuilder );
			}

			return new TracingILGenerator( this._packMethodBuilder, this.Trace, this._isDebuggable );
		}

		public TracingILGenerator GetUnpackFromMethodILGenerator()
		{
			if ( IsTraceEnabled )
			{
				this.Trace.WriteLine( "{0}::{1}", MethodBase.GetCurrentMethod(), this._unpackFromMethodBuilder );
			}
			
			return new TracingILGenerator( this._unpackFromMethodBuilder, this.Trace, this._isDebuggable );
		}

		public TracingILGenerator GetUnpackToMethodILGenerator()
		{
			if ( IsTraceEnabled )
			{
				this.Trace.WriteLine( "{0}::{1}", MethodBase.GetCurrentMethod(), this._unpackToMethodBuilder );
			}

			if ( this._unpackToMethodBuilder == null )
			{
				this._unpackToMethodBuilder =
					this._typeBuilder.DefineMethod(
						"UnpackToCore",
						MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.Final,
						CallingConventions.HasThis,
						null,
						new Type[] { typeof( Unpacker ), this._unpackFromMethodBuilder.ReturnType }
					);

				this._typeBuilder.DefineMethodOverride( this._unpackToMethodBuilder, this._typeBuilder.BaseType.GetMethod( this._unpackToMethodBuilder.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic ) );
			}

			return new TracingILGenerator( this._unpackToMethodBuilder, this.Trace, this._isDebuggable );
		}

		public ConstructorInfo Create()
		{
			if ( !this._typeBuilder.IsCreated() )
			{
				var il = this._constructorBuilder.GetILGenerator();
				il.Emit( OpCodes.Ldarg_0 );
				il.Emit( OpCodes.Call, this._typeBuilder.BaseType.GetConstructor( BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null ) );
				foreach ( var entry in this._serializers )
				{
					var targetType = Type.GetTypeFromHandle( entry.Key );
					var getMethod = Metadata._SerializationContext.GetSerializer1_Method.MakeGenericMethod( targetType );
					il.Emit( OpCodes.Ldarg_0 );
					il.Emit( OpCodes.Ldarg_1 );
					il.Emit( OpCodes.Callvirt, getMethod );
					il.Emit( OpCodes.Stfld, entry.Value );
				}
				il.Emit( OpCodes.Ret );
			}

			return this._typeBuilder.CreateType().GetConstructor( _constructorParameterTypes );
		}

		public MessagePackSerializer<T> CreateInstance<T>( SerializationContext context )
		{
			var contextParameter = Expression.Parameter( typeof( SerializationContext ) );
			return
				Expression.Lambda<Func<SerializationContext, MessagePackSerializer<T>>>(
					Expression.New(
						this.Create(),
						contextParameter
					),
					contextParameter
				).Compile()( context );
		}

		public FieldInfo RegisterSerializer( Type targetType )
		{
			if ( this._typeBuilder.IsCreated() )
			{
				throw new InvalidOperationException( "Type is already built." );
			}

			FieldBuilder result;
			if ( !this._serializers.TryGetValue( targetType.TypeHandle, out result ) )
			{
				result = this._typeBuilder.DefineField( "_serializer" + this._serializers.Count, typeof( MessagePackSerializer<> ).MakeGenericType( targetType ), FieldAttributes.Private | FieldAttributes.InitOnly );
				this._serializers.Add( targetType.TypeHandle, result );
			}

			return result;
		}
	}
}