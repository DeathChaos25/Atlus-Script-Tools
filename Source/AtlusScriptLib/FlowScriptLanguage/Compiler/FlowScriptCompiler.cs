﻿using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System;

using AtlusScriptLib.Common.Logging;
using AtlusScriptLib.Common.Registry;
using AtlusScriptLib.FlowScriptLanguage.Compiler.Parser;
using AtlusScriptLib.FlowScriptLanguage.Compiler.Processing;
using AtlusScriptLib.FlowScriptLanguage.Syntax;
using AtlusScriptLib.MessageScriptLanguage;
using AtlusScriptLib.MessageScriptLanguage.Compiler;

namespace AtlusScriptLib.FlowScriptLanguage.Compiler
{
    /// <summary>
    /// Represents the compiler for FlowScripts. Responsible for transforming FlowScript sources into code.
    /// </summary>
    public class FlowScriptCompiler
    {
        private static readonly Dictionary<FlowScriptValueType, FlowScriptValueType> sTypeToBaseTypeMap = new Dictionary<FlowScriptValueType, FlowScriptValueType>()
        {
            { FlowScriptValueType.Bool, FlowScriptValueType.Int },
            { FlowScriptValueType.Int, FlowScriptValueType.Int },
            { FlowScriptValueType.Float, FlowScriptValueType.Float },
            { FlowScriptValueType.String, FlowScriptValueType.Int },
        };

        //
        // compiler state state
        //
        private readonly Logger mLogger;
        private readonly FlowScriptFormatVersion mFormatVersion;
        private readonly HashSet<int> mImportedFileHashSet;
        private bool mReresolveImports;
        private string mFilePath;
        private string mCurrentBaseDirectory;
        private FlowScript mScript;
        private int mNextLabelIndex;
        private Stack<ScopeContext> mScopeStack;
        private ScopeContext mRootScope;
        private Variable mIntReturnValueVariable;
        private Variable mFloatReturnValueVariable;

        // variable indices
        private short mNextIntVariableIndex;
        private short mNextFloatVariableIndex;
        private short mNextStaticIntVariableIndex = 255;   // We count the indices for the static variables *down* to
        private short mNextStaticFloatVariableIndex = 255; // reduce the chance of conflict with the game's original scripts
        private short mNextIntParameterVariableIndex;
        private short mNextFloatParameterVariableIndex;

        //
        // procedure state
        //
        private FlowScriptProcedureDeclaration mProcedureDeclaration;
        private List<FlowScriptInstruction> mInstructions;
        private Dictionary<string, Label> mLabels;

        // tracing
        private int mStackValueCount; // for debugging
        private int mPUTFunctionIndex = -1;
        private int mPUTSFunctionIndex = -1;
        private int mPUTFFunctionIndex = -1;

        private ScopeContext Scope => mScopeStack.Peek();

        /// <summary>
        /// Gets or sets the encoding to use for any imported MessageScripts.
        /// </summary>
        public Encoding Encoding { get; set; }

        /// <summary>
        /// Gets or sets the library registry to use for any imported MessageScripts.
        /// </summary>
        public LibraryRegistry LibraryRegistry { get; set; }

        /// <summary>
        /// Gets or sets whether the compiler should output procedure tracing code.
        /// </summary>
        public bool EnableProcedureTracing { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the compiler should output procedure call tracing code.
        /// </summary>
        public bool EnableProcedureCallTracing { get; set; } = false;

        /// <summary>
        /// Gets or sets whether the compiler should output function call tracing code.
        /// </summary>
        public bool EnableFunctionCallTracing { get; set; } = false;

        /// <summary>
        /// Gets or sets whether the compiler should use stack cookies
        /// </summary>
        public bool EnableStackCookie { get; set; } = true;

        /// <summary>
        /// Initializes a FlowScript compiler with the given format version.
        /// </summary>
        /// <param name="version"></param>
        public FlowScriptCompiler( FlowScriptFormatVersion version )
        {
            mLogger = new Logger( nameof( FlowScriptCompiler ) );
            mFormatVersion = version;
            mImportedFileHashSet = new HashSet<int>();
        }

        /// <summary>
        /// Adds a compiler log listener. Use this if you want to see what went wrong during compilation.
        /// </summary>
        /// <param name="listener">The listener to add.</param>
        public void AddListener( LogListener listener )
        {
            listener.Subscribe( mLogger );
        }

        /// <summary>
        /// Tries to compile the provided FlowScript source. Returns a boolean indicating if the operation succeeded.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="flowScript"></param>
        /// <returns></returns>
        public bool TryCompile( string source, out FlowScript flowScript )
        {
            Info( "Start compiling FlowScript from source" );

            // Add source to prevent recursion
            mImportedFileHashSet.Add( source.GetHashCode() );

            // Parse compilation unit
            var parser = new FlowScriptCompilationUnitParser();
            parser.AddListener( new LoggerPassthroughListener( mLogger ) );
            if ( !parser.TryParse( source, out var compilationUnit ) )
            {
                Error( "Failed to parse compilation unit" );
                flowScript = null;
                return false;
            }

            return TryCompile( compilationUnit, out flowScript );
        }

        /// <summary>
        /// Tries to compile the provided FlowScript source. Returns a boolean indicating if the operation succeeded.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="flowScript"></param>
        /// <returns></returns>
        public bool TryCompile( Stream stream, out FlowScript flowScript )
        {
            if ( stream is FileStream fileStream )
            {
                mFilePath = Path.GetFullPath( fileStream.Name );
                mCurrentBaseDirectory = Path.GetDirectoryName( mFilePath );
                Info( $"Start compiling FlowScript from file '{mFilePath}'" );
                Info( $"Base directory set to '{mCurrentBaseDirectory}'" );
            }
            else
            {
                Info( "Start compiling FlowScript from stream" );
                Warning( "Because the input is not a file, this means imports will not work!" );
            }

            // Add hash for current file
            var hashAlgo = new MD5CryptoServiceProvider();
            var hashBytes = hashAlgo.ComputeHash( stream );
            int hashInt = BitConverter.ToInt32( hashBytes, 0 );
            mImportedFileHashSet.Add( hashInt );
            stream.Position = 0;

            // Parse compilation unit
            var parser = new FlowScriptCompilationUnitParser();
            parser.AddListener( new LoggerPassthroughListener( mLogger ) );
            if ( !parser.TryParse( stream, out var compilationUnit ) )
            {
                Error( "Failed to parse compilation unit" );
                flowScript = null;
                return false;
            }

            return TryCompile( compilationUnit, out flowScript );
        }

        /// <summary>
        /// Tries to compile the provided FlowScript source. Returns a boolean indicating if the operation succeeded.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="flowScript"></param>
        /// <returns></returns>
        public bool TryCompile( FlowScriptCompilationUnit compilationUnit, out FlowScript flowScript )
        {
            // Resolve types that are unresolved at parse time
            var resolver = new FlowScriptTypeResolver();
            resolver.AddListener( new LoggerPassthroughListener( mLogger ) );
            if ( !resolver.TryResolveTypes( compilationUnit ) )
            {
                Error( "Failed to resolve types in compilation unit" );
                flowScript = null;
                return false;
            }

            // Syntax checker?

            // Compile compilation unit
            if ( !TryCompileCompilationUnit( compilationUnit ) )
            {
                flowScript = null;
                return false;
            }

            flowScript = mScript;

            return true;
        }

        //
        // Compiling compilation units
        //
        private void InitializeCompilationState()
        {
            // todo: imports?
            mScript = new FlowScript( mFormatVersion );
            mNextLabelIndex = 0;

            // Set up scope stack
            mScopeStack = new Stack<ScopeContext>();

            // Create & push root scope
            // This is where all script-level declarations are stored
            mRootScope = new ScopeContext( null );
            mScopeStack.Push( mRootScope );
        }

        private bool TryCompileCompilationUnit( FlowScriptCompilationUnit compilationUnit )
        {
            Info( $"Start compiling FlowScript compilation unit with version {mFormatVersion}" );

            // Initialize
            InitializeCompilationState();

            // Resolve imports
            if ( compilationUnit.Imports.Count > 0 )
            {
                do
                {
                    if ( !TryResolveImports( compilationUnit ) )
                    {
                        Error( compilationUnit, "Failed to resolve imports" );
                        return false;
                    }
                } while ( mReresolveImports );
            }

            // Evaluate declarations, return values, parameters etc
            if ( !TryEvaluateCompilationUnitBeforeCompilation( compilationUnit ) )
                return false;

            // Set up tracing functions
            if ( Scope.TryGetFunction( "PUT", out var putFunction ) )
            {
                mPUTFunctionIndex = putFunction.Index;
            }

            // Set up tracing functions
            if ( Scope.TryGetFunction( "PUTS", out var putsFunction ) )
            {
                mPUTSFunctionIndex = putsFunction.Index;
            }

            // Set up tracing functions
            if ( Scope.TryGetFunction( "PUTF", out var putfFunction ) )
            {
                mPUTFFunctionIndex = putfFunction.Index;
            }

            // Compile compilation unit body
            foreach ( var statement in compilationUnit.Declarations )
            {
                if ( statement is FlowScriptProcedureDeclaration procedureDeclaration )
                {
                    if ( procedureDeclaration.Body != null )
                    {
                        if ( !TryCompileProcedure( procedureDeclaration, out var procedure ) )
                            return false;

                        mScript.Procedures.Add( procedure );
                    }
                }
                else if ( statement is FlowScriptVariableDeclaration variableDeclaration )
                {
                    if ( variableDeclaration.Initializer != null && ( variableDeclaration.Modifier != null && variableDeclaration.Modifier.ModifierType != FlowScriptModifierType.Constant ) )
                    {
                        Error( variableDeclaration.Initializer, "Non-constant variables declared outside of a procedure can't be initialized with a value" );
                        return false;
                    }
                }
                else if ( !( statement is FlowScriptFunctionDeclaration ) && !( statement is FlowScriptEnumDeclaration ) )
                {
                    Error( statement, $"Unexpected top-level statement type: {statement}" );
                    return false;
                }
            }

            Info( "Done compiling compilation unit" );

            return true;
        }

        private void ExpandImportStatementsPaths( FlowScriptCompilationUnit compilationUnit, string baseDirectory )
        {
            foreach ( var import in compilationUnit.Imports )
            {
                import.CompilationUnitFileName = Path.Combine( baseDirectory, import.CompilationUnitFileName );
            }
        }

        //
        // Resolving imports
        //
        private bool TryResolveImports( FlowScriptCompilationUnit compilationUnit )
        {
            Info( compilationUnit, "Resolving imports" );

            ExpandImportStatementsPaths( compilationUnit, Path.GetDirectoryName( mFilePath ) );

            var importedMessageScripts = new List<MessageScript>();
            var importedFlowScripts = new List<FlowScriptCompilationUnit>();

            foreach ( var import in compilationUnit.Imports )
            {
                if ( import.CompilationUnitFileName.EndsWith( ".msg" ) )
                {
                    // MessageScript
                    if ( !TryResolveMessageScriptImport( import, out var messageScript ) )
                    {
                        Error( import, $"Failed to resolve MessageScript import: { import.CompilationUnitFileName }" );
                        return false;
                    }

                    // Will be null if it was already imported before
                    if ( messageScript != null )
                        importedMessageScripts.Add( messageScript );
                }
                else if ( import.CompilationUnitFileName.EndsWith( ".flow" ) )
                {
                    // FlowScript
                    if ( !TryResolveFlowScriptImport( import, out var importedCompilationUnit ) )
                    {
                        Error( import, $"Failed to resolve FlowScript import: { import.CompilationUnitFileName }" );
                        return false;
                    }

                    // Will be null if it was already imported before
                    if ( importedCompilationUnit != null )
                        importedFlowScripts.Add( importedCompilationUnit );
                }
                else
                {
                    // Unknown
                    Error( import, $"Unknown import file type: {import.CompilationUnitFileName}" );
                    return false;
                }
            }

            // Resolve MessageScripts imports
            if ( importedMessageScripts.Count > 0 )
            {
                int startIndex = 0;
                if ( mScript.MessageScript == null )
                {
                    mScript.MessageScript = importedMessageScripts[ 0 ];
                    startIndex = 1;
                }

                // Merge message scripts
                for ( int i = startIndex; i < importedMessageScripts.Count; i++ )
                {
                    mScript.MessageScript.Windows.AddRange( importedMessageScripts[i].Windows );
                }
            }

            // Resolve FlowScript imports
            bool shouldReresolveImports = false;
            if ( importedFlowScripts.Count > 0 )
            {
                // Merge compilation units
                foreach ( var importedFlowScript in importedFlowScripts )
                {
                    if ( importedFlowScript.Imports.Count > 0 )
                    {
                        // If any of the imported FlowScripts have import, we have to re-resolve the imports again
                        shouldReresolveImports = true;
                        compilationUnit.Imports.AddRange( importedFlowScript.Imports );
                    }

                    compilationUnit.Declarations.AddRange( importedFlowScript.Declarations );
                }
            }

            mReresolveImports = shouldReresolveImports;

            if ( !mReresolveImports )
                Info( compilationUnit, "Done resolving imports" );

            return true;
        }

        private bool TryResolveMessageScriptImport( FlowScriptImport import, out MessageScript messageScript )
        {
            Info( $"Resolving MessageScript import '{import.CompilationUnitFileName}'" );

            var messageScriptCompiler = new MessageScriptCompiler( GetMessageScriptFormatVersion(), Encoding );
            messageScriptCompiler.AddListener( new LoggerPassthroughListener( mLogger ) );
            messageScriptCompiler.LibraryRegistry = LibraryRegistry;

            string compilationUnitFilePath = import.CompilationUnitFileName;

            if ( !File.Exists( compilationUnitFilePath ) )
            {
                // Retry as relative path if we have a filename
                if ( mFilePath != null )
                {
                    compilationUnitFilePath = Path.Combine( mCurrentBaseDirectory, compilationUnitFilePath );

                    if ( !File.Exists( compilationUnitFilePath ) )
                    {
                        Error( import, $"MessageScript file to import does not exist: {import.CompilationUnitFileName}" );
                        messageScript = null;
                        return false;
                    }
                }
                else
                {
                    Error( import, $"MessageScript file to import does not exist: {import.CompilationUnitFileName}" );
                    messageScript = null;
                    return false;
                }
            }

            Info( $"Importing MessageScript from file '{compilationUnitFilePath}'" );

            string messageScriptSource;

            try
            {
                messageScriptSource = File.ReadAllText( compilationUnitFilePath );
            }
            catch ( Exception )
            {
                Error( import, $"Can't open MessageScript file to import: {import.CompilationUnitFileName}" );
                messageScript = null;
                return false;
            }

            int messageScriptSourceHash = messageScriptSource.GetHashCode();

            if ( !mImportedFileHashSet.Contains( messageScriptSourceHash ) )
            {
                if ( !messageScriptCompiler.TryCompile( messageScriptSource, out messageScript ) )
                {
                    Error( import, $"Import MessageScript failed to compile: {import.CompilationUnitFileName}" );
                    return false;
                }

                mImportedFileHashSet.Add( messageScriptSourceHash );
            }
            else
            {
                Warning( $"MessageScript file '{compilationUnitFilePath}' was already included once! Skipping!" );
                messageScript = null;
            }

            return true;
        }

        private bool TryResolveFlowScriptImport( FlowScriptImport import, out FlowScriptCompilationUnit importedCompilationUnit )
        {
            string compilationUnitFilePath = import.CompilationUnitFileName;
            Info( $"Resolving FlowScript import '{compilationUnitFilePath}'" );

            if ( !File.Exists( compilationUnitFilePath ) )
            {
                // Retry as relative path if we have a filename
                if ( mFilePath != null )
                {
                    compilationUnitFilePath = Path.Combine( Path.GetDirectoryName(mFilePath), compilationUnitFilePath );

                    if ( !File.Exists( compilationUnitFilePath ) )
                    {
                        Error( import, $"FlowScript file to import does not exist: {import.CompilationUnitFileName}" );
                        importedCompilationUnit = null;
                        return false;
                    }
                }
                else
                {
                    Error( import, $"FlowScript file to import does not exist: {import.CompilationUnitFileName}" );
                    importedCompilationUnit = null;
                    return false;
                }
            }

            Info( $"Importing FlowScript from file '{compilationUnitFilePath}'" );
            FileStream flowScriptFileStream;
            try
            {
                flowScriptFileStream = File.Open( compilationUnitFilePath, FileMode.Open, FileAccess.Read, FileShare.Read );
            }
            catch ( Exception )
            {
                Error( import, $"Can't open FlowScript file to import: {import.CompilationUnitFileName}" );
                importedCompilationUnit = null;
                return false;
            }

            var hashAlgo = new MD5CryptoServiceProvider();
            var hashBytes = hashAlgo.ComputeHash( flowScriptFileStream );
            int flowScriptSourceHash = BitConverter.ToInt32( hashBytes, 0 );
            flowScriptFileStream.Position = 0;

            if ( !mImportedFileHashSet.Contains( flowScriptSourceHash ) )
            {
                var parser = new FlowScriptCompilationUnitParser();
                parser.AddListener( new LoggerPassthroughListener( mLogger ) );
                if ( !parser.TryParse( flowScriptFileStream, out importedCompilationUnit ) )
                {
                    Error( import, "Failed to parse imported FlowScript" );
                    return false;
                }

                flowScriptFileStream.Dispose();

                ExpandImportStatementsPaths( importedCompilationUnit, Path.GetDirectoryName( compilationUnitFilePath ) );

                mImportedFileHashSet.Add( flowScriptSourceHash );
            }
            else
            {
                Warning( $"FlowScript file '{compilationUnitFilePath}' was already included once! Skipping!" );
                importedCompilationUnit = null;
            }

            return true;
        }

        private MessageScriptFormatVersion GetMessageScriptFormatVersion()
        {
            switch ( mFormatVersion )
            {
                case FlowScriptFormatVersion.Version1:
                case FlowScriptFormatVersion.Version2:
                case FlowScriptFormatVersion.Version3:
                    return MessageScriptFormatVersion.Version1;
                case FlowScriptFormatVersion.Version1BigEndian:
                case FlowScriptFormatVersion.Version2BigEndian:
                case FlowScriptFormatVersion.Version3BigEndian:
                    return MessageScriptFormatVersion.Version1BigEndian;
            }

            return MessageScriptFormatVersion.Version1;
        }

        private bool TryEvaluateCompilationUnitBeforeCompilation( FlowScriptCompilationUnit compilationUnit )
        {
            // Declare constants for the message script window names
            if ( mScript.MessageScript != null )
            {
                Info( "Inserting MessageScript window identifier constants" );
                for ( int i = 0; i < mScript.MessageScript.Windows.Count; i++ )
                {
                    var window = mScript.MessageScript.Windows[i];

                    var declaration = new FlowScriptVariableDeclaration
                    (
                        new FlowScriptVariableModifier( FlowScriptModifierType.Constant ),
                        new FlowScriptTypeIdentifier( FlowScriptValueType.Int ),
                        new FlowScriptIdentifier( FlowScriptValueType.Int, window.Identifier ),
                        new FlowScriptIntLiteral( i )
                    );

                    if ( !Scope.TryDeclareVariable( declaration ) )
                    {
                        Error( declaration, $"Compiler generated constant for MessageScript window {window.Identifier} conflicts with another variable" );
                    }
                    else
                    {
                        Info( $"Declared compile time constant: {declaration}" );
                    }
                }
            }

            bool hasIntReturnValue = false;
            bool hasFloatReturnValue = false;
            short maxIntParameterCount = 0;
            short maxFloatParameterCount = 0;

            // top-level only
            Trace( "Registering script declarations" );
            foreach ( var statement in compilationUnit.Declarations )
            {
                switch ( statement )
                {
                    case FlowScriptFunctionDeclaration functionDeclaration:
                        {
                            if ( !Scope.TryDeclareFunction( functionDeclaration ) )
                            {
                                Warning( functionDeclaration, $"Ignoring duplicate function declaration: {functionDeclaration}" );
                            }
                            else
                            {
                                Trace( $"Registered function declaration '{functionDeclaration}'" );
                            }
                        }
                        break;
                    case FlowScriptProcedureDeclaration procedureDeclaration:
                        {
                            if ( !Scope.TryDeclareProcedure( procedureDeclaration ) )
                            {
                                Error( procedureDeclaration, $"Duplicate procedure declaration: {procedureDeclaration}" );
                                return false;
                            }
                            else
                            {
                                Trace( $"Registered procedure declaration '{procedureDeclaration}'" );
                            }

                            if ( procedureDeclaration.ReturnType.ValueType != FlowScriptValueType.Void )
                            {
                                if ( sTypeToBaseTypeMap[procedureDeclaration.ReturnType.ValueType] == FlowScriptValueType.Int )
                                {
                                    hasIntReturnValue = true;
                                }
                                else if ( procedureDeclaration.ReturnType.ValueType == FlowScriptValueType.Float )
                                {
                                    hasFloatReturnValue = true;
                                }
                            }

                            short intParameterCount = ( short )procedureDeclaration.Parameters.Count( x => sTypeToBaseTypeMap[x.Type.ValueType] == FlowScriptValueType.Int );
                            short floatParameterCount = ( short )procedureDeclaration.Parameters.Count( x => sTypeToBaseTypeMap[x.Type.ValueType] == FlowScriptValueType.Float );
                            maxIntParameterCount = Math.Max( intParameterCount, maxIntParameterCount );
                            maxFloatParameterCount = Math.Max( floatParameterCount, maxFloatParameterCount );
                        }
                        break;

                    case FlowScriptVariableDeclaration variableDeclaration:
                        {
                            if ( !TryRegisterVariableDeclaration( variableDeclaration ) )
                            {
                                Error( variableDeclaration, $"Duplicate variable declaration: {variableDeclaration}" );
                                return false;
                            }
                            else
                            {
                                Trace( $"Registered variable declaration '{variableDeclaration}'" );
                            }
                        }
                        break;

                    case FlowScriptEnumDeclaration enumDeclaration:
                        {
                            if ( !Scope.TryDeclareEnum( enumDeclaration ) )
                            {
                                Error( enumDeclaration, $"Failed to declare enum: {enumDeclaration}" );
                                return false;
                            }
                        }
                        break;
                }
            }

            // Add stuff from registry
            if ( LibraryRegistry != null )
            {
                // Functions
                foreach ( var libraryFunction in LibraryRegistry.FlowScriptLibraries.SelectMany( x => x.Functions ) )
                {
                    Scope.TryDeclareFunction( FlowScriptFunctionDeclaration.FromLibraryFunction( libraryFunction ) );
                }

                // Enums
                foreach ( var libraryEnum in LibraryRegistry.FlowScriptLibraries
                                                            .Where( x => x.Enums != null )
                                                            .SelectMany( x => x.Enums ) )
                {
                    Scope.TryDeclareEnum( FlowScriptEnumDeclaration.FromLibraryEnum( libraryEnum ) );
                }

                // Constants
                foreach ( var libraryConstant in LibraryRegistry.FlowScriptLibraries
                                                            .Where( x => x.Constants != null )
                                                            .SelectMany( x => x.Constants ) )
                {
                    Scope.TryDeclareVariable( FlowScriptVariableDeclaration.FromLibraryConstant( libraryConstant ) );
                }
            }

            // Declare return value variable
            if ( hasIntReturnValue )
            {
                mIntReturnValueVariable = Scope.GenerateVariable( FlowScriptValueType.Int, mNextIntVariableIndex++ );
            }

            if ( hasFloatReturnValue )
            {
                mFloatReturnValueVariable = Scope.GenerateVariable( FlowScriptValueType.Float, mNextFloatVariableIndex++ );
            }


            // Set up indices
            mNextIntParameterVariableIndex = mNextIntVariableIndex;
            mNextIntVariableIndex += maxIntParameterCount;

            mNextFloatParameterVariableIndex = mNextFloatVariableIndex;
            mNextFloatVariableIndex += maxFloatParameterCount;

            return true;
        }

        //
        // Procedure code generation
        //
        private void InitializeProcedureCompilationState( FlowScriptProcedureDeclaration declaration )
        {
            mProcedureDeclaration = declaration;
            mInstructions = new List<FlowScriptInstruction>();
            mLabels = new Dictionary<string, Label>();
            mStackValueCount = 1;
        }

        private bool TryCompileProcedure( FlowScriptProcedureDeclaration declaration, out FlowScriptProcedure procedure )
        {
            Info( declaration, $"Compiling procedure: {declaration.Identifier.Text}" );

            // Initialize procedure to null so we can return without having to set it explicitly
            procedure = null;

            // Compile procedure body
            if ( !TryEmitProcedureBody( declaration ) )
                return false;

            // Create labels
            if ( !TryResolveProcedureLabels( out var labels ) )
                return false;

            // Create the procedure object
            procedure = new FlowScriptProcedure( declaration.Identifier.Text, mInstructions, labels );

            return true;
        }

        private bool TryEmitProcedureBody( FlowScriptProcedureDeclaration declaration )
        {
            Trace( declaration.Body, $"Emitting procedure body for {declaration}" );

            // Initialize some state
            InitializeProcedureCompilationState( declaration );

            // Emit procedure start  
            PushScope();
            Emit( FlowScriptInstruction.PROC( mRootScope.Procedures[declaration.Identifier.Text].Index ) );

            if ( EnableProcedureTracing )
                TraceProcedureStart();

            // To mimick the official compiler
            //mNextLabelIndex++;

            if ( EnableStackCookie )
            {
                // Emit stack cookie
                Emit( FlowScriptInstruction.PUSHI( declaration.Identifier.Text.GetHashCode() ) );
            }

            // Register / forward declare labels in procedure body before codegen
            Trace( declaration.Body, "Forward declaring labels in procedure body" );
            if ( !TryRegisterLabels( declaration.Body ) )
            {
                Error( declaration.Body, "Failed to forward declare labels in procedure body" );
                return false;
            }

            // Emit procedure parameters
            if ( declaration.Parameters.Count > 0 )
            {
                Trace( declaration, "Emitting code for procedure parameters" );
                if ( !TryEmitProcedureParameters( declaration.Parameters ) )
                {
                    Error( declaration, "Failed to emit procedure parameters" );
                    return false;
                }
            }

            // Add implicit return
            if ( declaration.Body.Statements.Count == 0 || !( declaration.Body.Last() is FlowScriptReturnStatement ) )
            {
                Trace( declaration.Body, "Adding implicit return statement" );
                declaration.Body.Statements.Add( new FlowScriptReturnStatement() );
            }

            // Emit procedure body
            Trace( declaration.Body, "Emitting code for procedure body" );
            if ( !TryEmitCompoundStatement( declaration.Body ) )
            {
                Error( declaration.Body, "Failed to emit procedure body" );
                return false;
            }

            PopScope();

            return true;
        }

        private bool TryEmitProcedureParameters( List<FlowScriptParameter> parameters )
        {
            int intParameterCount = 0;
            int floatParameterCount = 0;

            foreach ( var parameter in parameters )
            {
                Trace( parameter, $"Emitting parameter: {parameter}" );

                // Create declaration
                var declaration = new FlowScriptVariableDeclaration(
                    new FlowScriptVariableModifier( FlowScriptModifierType.Local ),
                    parameter.Type,
                    parameter.Identifier,
                    null );

                // Declare variable
                if ( !TryEmitVariableDeclaration( declaration ) )
                    return false;

                // Push parameter value
                if ( sTypeToBaseTypeMap[declaration.Type.ValueType] == FlowScriptValueType.Int )
                {
                    Emit( FlowScriptInstruction.PUSHLIX( mNextIntParameterVariableIndex++ ) );
                    ++intParameterCount;
                }
                else
                {
                    Emit( FlowScriptInstruction.PUSHLFX( mNextFloatParameterVariableIndex++ ) );
                    ++floatParameterCount;
                }

                // Assign it with parameter value
                if ( !TryEmitVariableAssignment( declaration.Identifier ) )
                    return false;
            }

            // Reset parameter indices
            mNextIntParameterVariableIndex -= ( short )intParameterCount;
            mNextFloatParameterVariableIndex -= ( short )floatParameterCount;

            return true;
        }

        private bool TryRegisterLabels( FlowScriptCompoundStatement body )
        {
            foreach ( var declaration in body.Select( x => x as FlowScriptDeclaration ).Where( x => x != null ) )
            {
                if ( declaration.DeclarationType == FlowScriptDeclarationType.Label )
                {
                    mLabels[declaration.Identifier.Text] = CreateLabel( declaration.Identifier.Text );
                }
            }

            foreach ( var statement in body )
            {
                switch ( statement )
                {
                    case FlowScriptIfStatement ifStatement:
                        if ( !TryRegisterLabels( ifStatement.Body ) )
                            return false;

                        if ( ifStatement.ElseBody != null )
                        {
                            if ( !TryRegisterLabels( ifStatement.ElseBody ) )
                                return false;
                        }
                        break;

                    default:
                        break;
                }
            }

            return true;
        }

        private bool TryResolveProcedureLabels( out List<FlowScriptLabel> labels )
        {
            Trace( "Resolving labels in procedure" );
            if ( mLabels.Values.Any( x => !x.IsResolved ) )
            {
                foreach ( var item in mLabels.Values.Where( x => !x.IsResolved ) )
                    mLogger.Error( $"Label '{item.Name}' is referenced but not declared" );

                mLogger.Error( "Failed to compile procedure because one or more undeclared labels are referenced" );
                labels = null;
                return false;
            }

            labels = mLabels.Values
                .Select( x => new FlowScriptLabel( x.Name, x.InstructionIndex ) )
                .ToList();

            mLabels.Clear();
            return true;
        }

        //
        // Statements
        //
        private bool TryEmitStatements( IEnumerable<FlowScriptStatement> statements )
        {
            foreach ( var statement in statements )
            {
                if ( !TryEmitStatement( statement ) )
                    return false;
            }

            return true;
        }

        private bool TryEmitCompoundStatement( FlowScriptCompoundStatement compoundStatement )
        {
            PushScope();

            if ( !TryEmitStatements( compoundStatement ) )
                return false;

            PopScope();

            return true;
        }

        private bool TryEmitStatement( FlowScriptStatement statement )
        {
            switch ( statement )
            {
                case FlowScriptCompoundStatement compoundStatement:
                    if ( !TryEmitCompoundStatement( compoundStatement ) )
                        return false;
                    break;
                case FlowScriptDeclaration _:
                    {
                        if ( statement is FlowScriptVariableDeclaration variableDeclaration )
                        {
                            if ( !TryEmitVariableDeclaration( variableDeclaration ) )
                                return false;
                        }
                        else if ( statement is FlowScriptLabelDeclaration labelDeclaration )
                        {
                            if ( !TryRegisterLabelDeclaration( labelDeclaration ) )
                                return false;
                        }
                        else
                        {
                            Error( statement, "Expected variable or label declaration" );
                            return false;
                        }

                        break;
                    }

                case FlowScriptExpression expression:
                    if ( !TryEmitExpression( expression, true ) )
                        return false;
                    break;
                case FlowScriptIfStatement ifStatement:
                    if ( !TryEmitIfStatement( ifStatement ) )
                        return false;
                    break;
                case FlowScriptForStatement forStatement:
                    if ( !TryEmitForStatement( forStatement ) )
                        return false;
                    break;
                case FlowScriptWhileStatement whileStatement:
                    if ( !TryEmitWhileStatement( whileStatement ) )
                        return false;
                    break;
                case FlowScriptBreakStatement breakStatement:
                    if ( !TryEmitBreakStatement( breakStatement ) )
                        return false;
                    break;
                case FlowScriptContinueStatement continueStatement:
                    if ( !TryEmitContinueStatement( continueStatement ) )
                        return false;
                    break;
                case FlowScriptReturnStatement returnStatement:
                    if ( !TryEmitReturnStatement( returnStatement ) )
                    {
                        Error( returnStatement, $"Failed to compile return statement: {returnStatement}" );
                        return false;
                    }

                    break;
                case FlowScriptGotoStatement gotoStatement:
                    if ( !TryEmitGotoStatement( gotoStatement ) )
                    {
                        Error( gotoStatement, $"Failed to compile goto statement: {gotoStatement}" );
                        return false;
                    }

                    break;
                case FlowScriptSwitchStatement switchStatement:
                    if ( !TryEmitSwitchStatement( switchStatement ) )
                    {
                        Error( switchStatement, $"Failed to compile switch statement: {switchStatement}" );
                        return false;
                    }

                    break;
                default:
                    Error( statement, $"Compiling statement '{statement}' not implemented" );
                    return false;
            }

            return true;
        }

        //
        // Variable stuff
        //
        private bool TryGetVariableIndex( FlowScriptVariableDeclaration declaration, out short variableIndex )
        {
            if ( declaration.Modifier == null || declaration.Modifier.ModifierType == FlowScriptModifierType.Local )
            {
                // Local variable
                if ( declaration.Type.ValueType == FlowScriptValueType.Float )
                {
                    variableIndex = mNextFloatVariableIndex++;
                }
                else
                {
                    variableIndex = mNextIntVariableIndex++;
                }
            }
            else if ( declaration.Modifier.ModifierType == FlowScriptModifierType.Static )
            {
                // Static variable
                // We count the indices for the static variables *down* to
                // to reduce the chance we conflict with the game's original scripts
                if ( declaration.Type.ValueType == FlowScriptValueType.Float )
                {
                    variableIndex = mNextStaticFloatVariableIndex--;
                }
                else
                {
                    variableIndex = mNextStaticIntVariableIndex--;
                }
            }
            else if ( declaration.Modifier.ModifierType == FlowScriptModifierType.Constant )
            {
                // Constant
                variableIndex = -1;
            }
            else
            {
                Error( declaration.Modifier, $"Unexpected variable modifier: {declaration.Modifier}" );
                variableIndex = -1;
                return false;
            }

            return true;
        }

        private bool TryRegisterVariableDeclaration( FlowScriptVariableDeclaration declaration )
        {
            Trace( declaration, $"Registering variable declaration: {declaration}" );

            // Get variable idnex
            if ( !TryGetVariableIndex( declaration, out var variableIndex ) )
            {
                Error( declaration, $"Failed to get index for variable '{declaration}'" );
                return false;
            }

            // Declare variable in scope
            if ( !Scope.TryDeclareVariable( declaration, variableIndex ) )
            {
                Error( declaration, $"Variable '{declaration}' has already been declared" );
                return false;
            }

            return true;
        }

        private bool TryEmitVariableDeclaration( FlowScriptVariableDeclaration declaration )
        {
            Trace( declaration, $"Emitting variable declaration: {declaration}" );

            // Register variable
            if ( !TryRegisterVariableDeclaration( declaration ) )
            {
                Error( declaration, "Failed to register variable declaration" );
                return false;
            }

            // Nothing to emit for constants
            if ( declaration.Modifier.ModifierType == FlowScriptModifierType.Constant )
                return true;

            // Emit the variable initializer if it has one         
            if ( declaration.Initializer != null )
            {
                Trace( declaration.Initializer, "Emitting variable initializer" );

                if ( !TryEmitVariableAssignment( declaration.Identifier, declaration.Initializer, true ) )
                {
                    Error( declaration.Initializer, "Failed to emit code for variable initializer" );
                    return false;
                }
            }

            return true;
        }

        private bool TryRegisterLabelDeclaration( FlowScriptLabelDeclaration declaration )
        {
            Trace( declaration, $"Registering label declaration: {declaration}" );

            // register label
            if ( !mLabels.TryGetValue( declaration.Identifier.Text, out var label ) )
            {
                Error( declaration.Identifier, $"Unexpected declaration of an registered label: '{declaration}'" );
                return false;
            }

            ResolveLabel( label );

            return true;
        }

        //
        // Expressions
        //
        private bool TryEmitExpression( FlowScriptExpression expression, bool isStatement )
        {
            switch ( expression )
            {
                case FlowScriptMemberAccessExpression memberAccessExpression:
                    if ( isStatement )
                    {
                        Error( memberAccessExpression, "An identifier is an invalid statement" );
                        return false;
                    }

                    if ( !TryEmitMemberAccess( memberAccessExpression ) )
                        return false;
                    break;

                case FlowScriptCallOperator callExpression:
                    if ( !TryEmitCall( callExpression, isStatement ) )
                        return false;
                    break;
                case FlowScriptUnaryExpression unaryExpression:
                    if ( !TryEmitUnaryExpression( unaryExpression, isStatement ) )
                        return false;
                    break;
                case FlowScriptBinaryExpression binaryExpression:
                    if ( !TryEmitBinaryExpression( binaryExpression, isStatement ) )
                        return false;
                    break;
                case FlowScriptIdentifier identifier:
                    if ( isStatement )
                    {
                        Error( identifier, "An identifier is an invalid statement" );
                        return false;
                    }

                    if ( !TryEmitPushVariableValue( identifier ) )
                        return false;
                    break;
                case FlowScriptBoolLiteral boolLiteral:
                    if ( isStatement )
                    {
                        Error( boolLiteral, "A boolean literal is an invalid statement" );
                        return false;
                    }

                    EmitPushBoolLiteral( boolLiteral );
                    break;
                case FlowScriptIntLiteral intLiteral:
                    if ( isStatement )
                    {
                        Error( intLiteral, "A integer literal is an invalid statement" );
                        return false;
                    }

                    EmitPushIntLiteral( intLiteral );
                    break;
                case FlowScriptFloatLiteral floatLiteral:
                    if ( isStatement )
                    {
                        Error( floatLiteral, "A float literal is an invalid statement" );
                        return false;
                    }

                    EmitPushFloatLiteral( floatLiteral );
                    break;
                case FlowScriptStringLiteral stringLiteral:
                    if ( isStatement )
                    {
                        Error( stringLiteral, "A string literal is an invalid statement" );
                        return false;
                    }

                    EmitPushStringLiteral( stringLiteral );
                    break;
                default:
                    Error( expression, $"Compiling expression '{expression}' not implemented" );
                    return false;
            }

            return true;
        }

        private bool TryEmitMemberAccess( FlowScriptMemberAccessExpression memberAccessExpression )
        {
            Trace( memberAccessExpression, $"Emitting member access '{memberAccessExpression}'" );

            if ( !Scope.TryGetEnum( memberAccessExpression.Operand.Text, out var enumType ) )
            {
                Error( $"Referenced undeclared enum '{memberAccessExpression.Operand.Text}'" );
                return false;
            }

            if ( !enumType.Members.TryGetValue( memberAccessExpression.Member.Text, out var value ) )
            {
                Error( $"Referenced undeclared enum member '{memberAccessExpression.Member.Text}' in enum '{memberAccessExpression.Operand.Text}'" );
                return false;
            }

            if ( !TryEmitExpression( value, false ) )
            {
                Error( $"Failed to emit enum value '{value}'" );
                return false;
            }

            return true;
        }

        private bool TryEmitCall( FlowScriptCallOperator callExpression, bool isStatement )
        {
            Trace( callExpression, $"Emitting call: {callExpression}" );

            if ( mRootScope.TryGetFunction( callExpression.Identifier.Text, out var function ) )
            {
                if ( callExpression.Arguments.Count != function.Declaration.Parameters.Count )
                {
                    Error( $"Function '{function.Declaration}' expects {function.Declaration.Parameters.Count} arguments but {callExpression.Arguments.Count} are given" );
                    return false;
                }

                // Check MessageScript function call semantics
                if ( mScript.MessageScript != null )
                {
                    // Todo: make this less hardcoded
                    switch ( callExpression.Identifier.Text )
                    {
                        case "MSG":
                        case "SEL":
                            {
                                var firstArgument = callExpression.Arguments[ 0 ];
                                if ( firstArgument is FlowScriptIntLiteral firstArgumentInt )
                                {
                                    var index = firstArgumentInt.Value;
                                    if ( index < 0 || index >= mScript.MessageScript.Windows.Count )
                                    {
                                        Error( $"Function call to {callExpression.Identifier.Text} references message that doesn't exist (index: {index})" );
                                        return false;
                                    }

                                    var expectedWindowType = callExpression.Identifier.Text == "MSG"
                                        ? MessageScriptWindowType.Dialogue
                                        : MessageScriptWindowType.Selection;

                                    var window = mScript.MessageScript.Windows[index];
                                    if ( window.Type != expectedWindowType )
                                    {
                                        Error( $"Function call to {callExpression.Identifier.Text} doesn't reference a {expectedWindowType} message, got message of type: {window.Type} index: {index}" );
                                        return false;
                                    }
                                }
                            }
                            break;
                    }
                }

                if ( function.Declaration.Parameters.Count > 0 )
                {
                    if ( !TryEmitFunctionCallArguments( callExpression ) )
                        return false;
                }

                // call function
                if ( EnableFunctionCallTracing )
                {
                    TraceFunctionCall( function.Declaration );
                }

                Emit( FlowScriptInstruction.COMM( function.Index ) );

                if ( !isStatement && function.Declaration.ReturnType.ValueType != FlowScriptValueType.Void )
                {
                    // push return value of function
                    Trace( callExpression, $"Emitting PUSHREG for {callExpression}" );
                    Emit( FlowScriptInstruction.PUSHREG() );

                    if ( EnableFunctionCallTracing )
                    {
                        TraceFunctionCallReturnValue( function.Declaration );
                    }
                }
            }
            else if ( mRootScope.TryGetProcedure( callExpression.Identifier.Text, out var procedure ) )
            {
                if ( callExpression.Arguments.Count != procedure.Declaration.Parameters.Count )
                {
                    Error( $"Procedure '{procedure.Declaration}' expects {procedure.Declaration.Parameters.Count} arguments but {callExpression.Arguments.Count} are given" );
                    return false;
                }

                if ( procedure.Declaration.Parameters.Count > 0 )
                {
                    if ( !TryEmitParameterCallArguments( callExpression, procedure.Declaration ) )
                        return false;
                }

                // call procedure
                if ( EnableProcedureCallTracing )
                {
                    TraceProcedureCall( procedure.Declaration );
                }

                Emit( FlowScriptInstruction.CALL( procedure.Index ) );

                if ( !isStatement && procedure.Declaration.ReturnType.ValueType != FlowScriptValueType.Void )
                {
                    // Push return value of procedure
                    if ( sTypeToBaseTypeMap[procedure.Declaration.ReturnType.ValueType] == FlowScriptValueType.Int )
                        Emit( FlowScriptInstruction.PUSHLIX( mIntReturnValueVariable.Index ) );
                    else
                        Emit( FlowScriptInstruction.PUSHLFX( mFloatReturnValueVariable.Index ) );

                    if ( EnableProcedureCallTracing )
                    {
                        TraceProcedureCallReturnValue( procedure.Declaration );
                    }
                }
            }
            else
            {
                Error( callExpression, $"Invalid call expression. Expected function or procedure identifier, got: {callExpression.Identifier}" );
                return false;
            }

            return true;
        }

        private bool TryEmitFunctionCallArguments( FlowScriptCallOperator callExpression )
        {
            Trace( "Emitting function call arguments" );

            // Compile expressions backwards so they are pushed to the stack in the right order
            for ( int i = callExpression.Arguments.Count - 1; i >= 0; i-- )
            {
                if ( !TryEmitExpression( callExpression.Arguments[i], false ) )
                {
                    Error( callExpression.Arguments[i], $"Failed to compile function call argument: {callExpression.Arguments[i]}" );
                    return false;
                }
            }

            return true;
        }

        private bool TryEmitParameterCallArguments( FlowScriptCallOperator callExpression, FlowScriptProcedureDeclaration declaration )
        {
            Trace( "Emitting parameter call arguments" );

            int intParameterCount = 0;
            int floatParameterCount = 0;

            for ( int i = 0; i < callExpression.Arguments.Count; i++ )
            {
                if ( !TryEmitExpression( callExpression.Arguments[i], false ) )
                {
                    Error( callExpression.Arguments[i], $"Failed to compile function call argument: {callExpression.Arguments[i]}" );
                    return false;
                }

                // Assign each required parameter variable
                if ( sTypeToBaseTypeMap[declaration.Parameters[i].Type.ValueType] == FlowScriptValueType.Int )
                {
                    Emit( FlowScriptInstruction.POPLIX( mNextIntParameterVariableIndex++ ) );
                    ++intParameterCount;
                }
                else
                {
                    Emit( FlowScriptInstruction.POPLFX( mNextFloatParameterVariableIndex++ ) );
                    ++floatParameterCount;
                }
            }

            // Reset the parameter variable indices
            mNextIntParameterVariableIndex -= ( short )intParameterCount;
            mNextFloatParameterVariableIndex -= ( short )floatParameterCount;

            return true;
        }

        private bool TryEmitUnaryExpression( FlowScriptUnaryExpression unaryExpression, bool isStatement )
        {
            Trace( unaryExpression, $"Emitting unary expression: {unaryExpression}" );

            switch ( unaryExpression )
            {
                case FlowScriptPostfixOperator postfixOperator:
                    if ( !TryEmitPostfixOperator( postfixOperator, isStatement ) )
                    {
                        Error( postfixOperator, "Failed to emit postfix operator" );
                        return false;
                    }
                    break;

                case FlowScriptPrefixOperator prefixOperator:
                    if ( !TryEmitPrefixOperator( prefixOperator, isStatement ) )
                    {
                        Error( prefixOperator, "Failed to emit prefix operator" );
                        return false;
                    }
                    break;

                default:
                    Error( unaryExpression, $"Emitting unary expression '{unaryExpression}' not implemented" );
                    return false;
            }

            return true;
        }

        private bool TryEmitPostfixOperator( FlowScriptPostfixOperator postfixOperator, bool isStatement )
        {
            var identifier = ( FlowScriptIdentifier )postfixOperator.Operand;
            if ( !Scope.TryGetVariable( identifier.Text, out var variable ) )
            {
                Error( identifier, $"Reference to undefined variable: {identifier}" );
                return false;
            }

            short index;
            if ( variable.Declaration.Type.ValueType != FlowScriptValueType.Float )
            {
                index = mNextIntVariableIndex++;
            }
            else
            {
                index = mNextFloatVariableIndex++;
            }

            Variable copy = null;
            if ( !isStatement )
            {
                // Make copy of variable
                copy = Scope.GenerateVariable( variable.Declaration.Type.ValueType, index );

                // Push value of the variable to save in the copy
                if ( !TryEmitPushVariableValue( identifier ) )
                {
                    Error( identifier, $"Failed to push variable value to copy variable: {identifier}" );
                    return false;
                }

                // Assign the copy with the value of the variable
                if ( !TryEmitVariableAssignment( copy.Declaration.Identifier ) )
                {
                    Error( $"Failed to emit variable assignment to copy variable: {copy}" );
                    return false;
                }
            }

            // In/decrement the actual variable
            {
                // Push 1
                Emit( FlowScriptInstruction.PUSHIS( 1 ) );

                // Push value of the variable
                if ( !TryEmitPushVariableValue( identifier ) )
                {
                    Error( identifier, $"Failed to push variable value to copy variable: {identifier}" );
                    return false;
                }

                // Subtract or add
                if ( postfixOperator is FlowScriptPostfixDecrementOperator )
                {
                    Emit( FlowScriptInstruction.SUB() );
                }
                else if ( postfixOperator is FlowScriptPostfixIncrementOperator )
                {
                    Emit( FlowScriptInstruction.ADD() );
                }
                else
                {
                    return false;
                }

                // Emit assignment with calculated value
                if ( !TryEmitVariableAssignment( identifier ) )
                {
                    Error( identifier, $"Failed to emit variable assignment: {identifier}" );
                    return false;
                }
            }

            if ( !isStatement )
            {
                // Push the value of the copy
                Trace( $"Pushing variable value: {copy.Declaration.Identifier}" );

                if ( !TryEmitPushVariableValue( copy.Declaration.Identifier ) )
                {
                    Error( $"Failed to push value for copy variable { copy }" );
                    return false;
                }
            }

            return true;
        }

        private bool TryEmitPrefixOperator( FlowScriptPrefixOperator prefixOperator, bool isStatement )
        {
            switch ( prefixOperator )
            {
                case FlowScriptLogicalNotOperator _:
                case FlowScriptNegationOperator _:
                    if ( isStatement )
                    {
                        Error( prefixOperator, "A logical not operator is an invalid statement" );
                        return false;
                    }

                    if ( !TryEmitExpression( prefixOperator.Operand, false ) )
                    {
                        Error( prefixOperator.Operand, "Failed to emit operand for unary expression" );
                        return false;
                    }

                    if ( prefixOperator is FlowScriptLogicalNotOperator )
                    {
                        Trace( prefixOperator, "Emitting NOT" );
                        Emit( FlowScriptInstruction.NOT() );
                    }
                    else if ( prefixOperator is FlowScriptNegationOperator )
                    {
                        Trace( prefixOperator, "Emitting MINUS" );
                        Emit( FlowScriptInstruction.MINUS() );
                    }
                    else
                    {
                        goto default;
                    }
                    break;

                case FlowScriptPrefixDecrementOperator _:
                case FlowScriptPrefixIncrementOperator _:
                    {
                        // Push 1
                        Emit( FlowScriptInstruction.PUSHIS( 1 ) );

                        // Push value
                        var identifier = ( FlowScriptIdentifier )prefixOperator.Operand;
                        if ( !TryEmitPushVariableValue( identifier ) )
                        {
                            Error( identifier, $"Failed to emit variable value for: { identifier }" );
                            return false;
                        }

                        // Emit operation
                        if ( prefixOperator is FlowScriptPrefixDecrementOperator )
                        {
                            Emit( FlowScriptInstruction.SUB() );
                        }
                        else if ( prefixOperator is FlowScriptPrefixIncrementOperator )
                        {
                            Emit( FlowScriptInstruction.ADD() );
                        }
                        else
                        {
                            goto default;
                        }

                        // Emit assignment
                        if ( !TryEmitVariableAssignment( identifier ) )
                        {
                            Error( prefixOperator, $"Failed to emit variable assignment: {prefixOperator}" );
                            return false;
                        }

                        if ( !isStatement )
                        {
                            Trace( prefixOperator, $"Emitting variable value: {identifier}" );

                            if ( !TryEmitPushVariableValue( identifier ) )
                            {
                                Error( identifier, $"Failed to emit variable value for: { identifier }" );
                                return false;
                            }
                        }
                    }
                    break;

                default:
                    Error( prefixOperator, $"Unknown prefix operator: {prefixOperator}" );
                    return false;
            }

            return true;
        }

        private bool TryEmitBinaryExpression( FlowScriptBinaryExpression binaryExpression, bool isStatement )
        {
            Trace( binaryExpression, $"Emitting binary expression: {binaryExpression}" );

            if ( binaryExpression is FlowScriptAssignmentOperatorBase assignment )
            {
                if ( !TryEmitVariableAssignmentBase( assignment, isStatement ) )
                {
                    Error( assignment, $"Failed to emit variable assignment: { assignment }" );
                    return false;
                }
            }
            else
            {
                if ( isStatement )
                {
                    Error( binaryExpression, "A binary operator is not a valid statement" );
                    return false;
                }
                else
                {
                    Trace( "Emitting value for binary expression" );
                }

                if ( !TryEmitExpression( binaryExpression.Right, false ) )
                {
                    Error( binaryExpression.Right, $"Failed to emit right expression: {binaryExpression.Left}" );
                    return false;
                }

                if ( !TryEmitExpression( binaryExpression.Left, false ) )
                {
                    Error( binaryExpression.Right, $"Failed to emit left expression: {binaryExpression.Right}" );
                    return false;
                }

                switch ( binaryExpression )
                {
                    case FlowScriptAdditionOperator _:
                        Emit( FlowScriptInstruction.ADD() );
                        break;
                    case FlowScriptSubtractionOperator _:
                        Emit( FlowScriptInstruction.SUB() );
                        break;
                    case FlowScriptMultiplicationOperator _:
                        Emit( FlowScriptInstruction.MUL() );
                        break;
                    case FlowScriptDivisionOperator _:
                        Emit( FlowScriptInstruction.DIV() );
                        break;
                    case FlowScriptLogicalOrOperator _:
                        Emit( FlowScriptInstruction.OR() );
                        break;
                    case FlowScriptLogicalAndOperator _:
                        Emit( FlowScriptInstruction.AND() );
                        break;
                    case FlowScriptEqualityOperator _:
                        Emit( FlowScriptInstruction.EQ() );
                        break;
                    case FlowScriptNonEqualityOperator _:
                        Emit( FlowScriptInstruction.NEQ() );
                        break;
                    case FlowScriptLessThanOperator _:
                        Emit( FlowScriptInstruction.S() );
                        break;
                    case FlowScriptGreaterThanOperator _:
                        Emit( FlowScriptInstruction.L() );
                        break;
                    case FlowScriptLessThanOrEqualOperator _:
                        Emit( FlowScriptInstruction.SE() );
                        break;
                    case FlowScriptGreaterThanOrEqualOperator _:
                        Emit( FlowScriptInstruction.LE() );
                        break;
                    default:
                        Error( binaryExpression, $"Emitting binary expression '{binaryExpression}' not implemented" );
                        return false;
                }
            }

            return true;
        }

        private bool TryEmitPushVariableValue( FlowScriptIdentifier identifier )
        {
            Trace( identifier, $"Emitting variable reference: {identifier}" );

            if ( !Scope.TryGetVariable( identifier.Text, out var variable ) )
            {
                Error( identifier, $"Referenced undeclared variable '{identifier}'" );
                return false;
            }

            if ( variable.Declaration.Modifier == null || variable.Declaration.Modifier.ModifierType == FlowScriptModifierType.Local )
            {
                if ( variable.Declaration.Type.ValueType != FlowScriptValueType.Float )
                    Emit( FlowScriptInstruction.PUSHLIX( variable.Index ) );
                else
                    Emit( FlowScriptInstruction.PUSHLFX( variable.Index ) );
            }
            else if ( variable.Declaration.Modifier.ModifierType == FlowScriptModifierType.Static )
            {
                if ( variable.Declaration.Type.ValueType != FlowScriptValueType.Float )
                    Emit( FlowScriptInstruction.PUSHIX( variable.Index ) );
                else
                    Emit( FlowScriptInstruction.PUSHIF( variable.Index ) );
            }
            else if ( variable.Declaration.Modifier.ModifierType == FlowScriptModifierType.Constant )
            {
                if ( !TryEmitExpression( variable.Declaration.Initializer, false ) )
                {
                    Error( variable.Declaration.Initializer, $"Failed to emit value for constant expression: {variable.Declaration}" );
                    return false;
                }
            }
            else
            {
                Error( variable.Declaration, "Unsupported variable modifier type" );
                return false;
            }

            return true;
        }

        private bool TryEmitVariableAssignmentBase( FlowScriptAssignmentOperatorBase assignment, bool isStatement )
        {
            if ( assignment is FlowScriptCompoundAssignmentOperator compoundAssignment )
            {
                if ( !TryEmitVariableCompoundAssignment( compoundAssignment, isStatement ) )
                {
                    Error( compoundAssignment, $"Failed to emit compound assignment: {compoundAssignment}" );
                    return false;
                }
            }
            else
            {
                if ( !TryEmitVariableAssignment( ( FlowScriptIdentifier )assignment.Left, assignment.Right, isStatement ) )
                {
                    Error( assignment, $"Failed to emit assignment: {assignment}" );
                    return false;
                }
            }

            return true;
        }

        private bool TryEmitVariableCompoundAssignment( FlowScriptCompoundAssignmentOperator compoundAssignment, bool isStatement )
        {
            Trace( compoundAssignment, $"Emitting compound assignment: {compoundAssignment}" );

            // Push value of right expression
            if ( !TryEmitExpression( compoundAssignment.Right, false ) )
            {
                Error( compoundAssignment.Right, $"Failed to emit expression: { compoundAssignment.Right }" );
                return false;
            }

            // Push value of variable
            var identifier = ( FlowScriptIdentifier )compoundAssignment.Left;
            if ( !TryEmitPushVariableValue( identifier ) )
            {
                Error( identifier, $"Failed to emit variable value for: { identifier }" );
                return false;
            }

            // Emit operation
            switch ( compoundAssignment )
            {
                case FlowScriptAdditionAssignmentOperator _:
                    Emit( FlowScriptInstruction.ADD() );
                    break;

                case FlowScriptSubtractionAssignmentOperator _:
                    Emit( FlowScriptInstruction.SUB() );
                    break;

                case FlowScriptMultiplicationAssignmentOperator _:
                    Emit( FlowScriptInstruction.MUL() );
                    break;

                case FlowScriptDivisionAssignmentOperator _:
                    Emit( FlowScriptInstruction.DIV() );
                    break;

                default:
                    Error( compoundAssignment, $"Unknown compound assignment type: { compoundAssignment }" );
                    return false;
            }

            // Assign the value to the variable
            if ( !TryEmitVariableAssignment( identifier ) )
            {
                Error( identifier, $"Failed to assign value to variable: { identifier }" );
                return false;
            }

            if ( !isStatement )
            {
                Trace( compoundAssignment, $"Pushing variable value: {identifier}" );

                // Push value of variable
                if ( !TryEmitPushVariableValue( identifier ) )
                {
                    Error( identifier, $"Failed to emit variable value for: { identifier }" );
                    return false;
                }
            }

            return true;
        }


        /// <summary>
        /// Emit variable assignment with an explicit expression.
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="expression"></param>
        /// <returns></returns>
        private bool TryEmitVariableAssignment( FlowScriptIdentifier identifier, FlowScriptExpression expression, bool isStatement )
        {
            Trace( $"Emitting variable assignment: {identifier} = {expression}" );

            if ( !TryEmitExpression( expression, false ) )
            {
                Error( expression, "Failed to emit code for assigment value expression" );
                return false;
            }

            if ( !TryEmitVariableAssignment( identifier ) )
            {
                Error( identifier, "Failed to emit code for value assignment to variable" );
                return false;
            }

            if ( !isStatement )
            {
                // Push value of variable
                Trace( identifier, $"Pushing variable value: {identifier}" );

                if ( !TryEmitPushVariableValue( identifier ) )
                {
                    Error( identifier, $"Failed to emit variable value for: { identifier }" );
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Emit variable assignment without explicit expression.
        /// </summary>
        /// <param name="identifier"></param>
        /// <returns></returns>
        private bool TryEmitVariableAssignment( FlowScriptIdentifier identifier )
        {
            if ( !Scope.TryGetVariable( identifier.Text, out var variable ) )
            {
                Error( identifier, $"Assignment to undeclared variable: {identifier}" );
                return false;
            }

            if ( !TryEmitVariableAssignment( variable.Declaration, variable.Index ) )
                return false;

            return true;
        }

        private bool TryEmitVariableAssignment( FlowScriptVariableDeclaration declaration, short index )
        {
            // load the value into the variable
            if ( declaration.Modifier == null || declaration.Modifier.ModifierType == FlowScriptModifierType.Local )
            {
                if ( declaration.Type.ValueType != FlowScriptValueType.Float )
                    Emit( FlowScriptInstruction.POPLIX( index ) );
                else
                    Emit( FlowScriptInstruction.POPLFX( index ) );
            }
            else if ( declaration.Modifier.ModifierType == FlowScriptModifierType.Static )
            {
                if ( declaration.Type.ValueType != FlowScriptValueType.Float )
                    Emit( FlowScriptInstruction.POPIX( index ) );
                else
                    Emit( FlowScriptInstruction.POPFX( index ) );
            }
            else if ( declaration.Modifier.ModifierType == FlowScriptModifierType.Constant )
            {
                Error( declaration.Identifier, "Illegal assignment to constant" );
                return false;
            }
            else
            {
                Error( declaration.Identifier, $"Unsupported variable modifier type: {declaration.Modifier}" );
                return false;
            }

            return true;
        }

        //
        // Literal values
        //
        private void EmitPushBoolLiteral( FlowScriptBoolLiteral boolLiteral )
        {
            Trace( boolLiteral, $"Pushing bool literal: {boolLiteral}" );

            if ( boolLiteral.Value )
                Emit( FlowScriptInstruction.PUSHIS( 1 ) );
            else
                Emit( FlowScriptInstruction.PUSHIS( 0 ) );
        }

        private void EmitPushIntLiteral( FlowScriptIntLiteral intLiteral )
        {
            Trace( intLiteral, $"Pushing int literal: {intLiteral}" );

            if ( IntFitsInShort( intLiteral.Value ) )
                Emit( FlowScriptInstruction.PUSHIS( ( short )intLiteral.Value ) );
            else
                Emit( FlowScriptInstruction.PUSHI( intLiteral.Value ) );
        }

        private void EmitPushFloatLiteral( FlowScriptFloatLiteral floatLiteral )
        {
            Trace( floatLiteral, $"Pushing float literal: {floatLiteral}" );

            Emit( FlowScriptInstruction.PUSHF( floatLiteral.Value ) );
        }

        private void EmitPushStringLiteral( FlowScriptStringLiteral stringLiteral )
        {
            Trace( stringLiteral, $"Pushing string literal: {stringLiteral}" );

            Emit( FlowScriptInstruction.PUSHSTR( stringLiteral.Value ) );
        }

        private bool IntFitsInShort( int value )
        {
            return ( ( ( value & 0xffff8000 ) + 0x8000 ) & 0xffff7fff ) == 0;
        }

        // 
        // If statement
        //
        private bool TryEmitIfStatement( FlowScriptIfStatement ifStatement )
        {
            Trace( ifStatement, $"Emitting if statement: '{ifStatement}'" );

            // emit condition expression, which should push a boolean value to the stack
            if ( !TryEmitExpression( ifStatement.Condition, false ) )
            {
                Error( ifStatement.Condition, "Failed to emit if statement condition" );
                return false;
            }

            // generate label for jump if condition is false
            var endLabel = CreateLabel( "IfEndLabel" );
            Label elseLabel = null;

            // emit if instruction that jumps to the label if the condition is false
            if ( ifStatement.ElseBody == null )
            {
                Emit( FlowScriptInstruction.IF( endLabel.Index ) );
            }
            else
            {
                elseLabel = CreateLabel( "IfElseLabel" );
                Emit( FlowScriptInstruction.IF( elseLabel.Index ) );
            }

            // compile if body
            if ( ifStatement.ElseBody == null )
            {
                // If there's no else, then the end of the body will line up with the end label
                if ( !TryEmitIfStatementBody( ifStatement.Body, null ) )
                    return false;
            }
            else
            {
                // If there's an else body, then the end of the body will line up with the else label, but it should line up with the end label
                if ( !TryEmitIfStatementBody( ifStatement.Body, endLabel ) )
                    return false;
            }

            if ( ifStatement.ElseBody != null )
            {
                ResolveLabel( elseLabel );

                // compile if else body
                // The else body will always line up with the end label
                if ( !TryEmitIfStatementBody( ifStatement.ElseBody, null ) )
                    return false;
            }

            ResolveLabel( endLabel );

            return true;
        }

        private bool TryEmitIfStatementBody( FlowScriptCompoundStatement body, Label endLabel )
        {
            Trace( body, "Compiling if statement body" );
            if ( !TryEmitCompoundStatement( body ) )
            {
                Error( body, "Failed to compile if statement body" );
                return false;
            }

            // ensure that we end up at the right position after the body
            if ( endLabel != null )
                Emit( FlowScriptInstruction.GOTO( endLabel.Index ) );

            return true;
        }

        // 
        // If statement
        //
        private bool TryEmitForStatement( FlowScriptForStatement forStatement )
        {
            Trace( forStatement, $"Emitting for statement: '{forStatement}'" );

            // Enter for scope
            PushScope();

            // Emit initializer
            if ( !TryEmitStatement( forStatement.Initializer ) )
            {
                Error( forStatement.Condition, "Failed to emit for statement initializer" );
                return false;
            }

            // Create labels
            var conditionLabel = CreateLabel( "ForConditionLabel" );
            var afterLoopLabel = CreateLabel( "ForAfterLoopLabel" );
            var endLabel = CreateLabel( "ForEndLabel" );

            // Emit condition check
            {
                ResolveLabel( conditionLabel );

                // Emit condition
                if ( !TryEmitExpression( forStatement.Condition, false ) )
                {
                    Error( forStatement.Condition, "Failed to emit for statement condition" );
                    return false;
                }

                // Jump to the end of the loop if condition is NOT true
                Emit( FlowScriptInstruction.IF( endLabel.Index ) );
            }

            // Emit body
            {
                // Allow break & continue
                Scope.BreakLabel = endLabel;
                Scope.ContinueLabel = afterLoopLabel;

                // emit body
                Trace( forStatement.Body, "Emitting for statement body" );
                if ( !TryEmitCompoundStatement( forStatement.Body ) )
                {
                    Error( forStatement.Body, "Failed to emit for statement body" );
                    return false;
                }
            }

            // Emit after loop
            {
                ResolveLabel( afterLoopLabel );

                if ( !TryEmitExpression( forStatement.AfterLoop, true ) )
                {
                    Error( forStatement.AfterLoop, "Failed to emit for statement after loop expression" );
                    return false;
                }

                // jump to condition check
                Emit( FlowScriptInstruction.GOTO( conditionLabel.Index ) );
            }

            // We're at the end of the for loop
            ResolveLabel( endLabel );

            // Exit for scope
            PopScope();

            return true;
        }

        // 
        // While statement
        //
        private bool TryEmitWhileStatement( FlowScriptWhileStatement whileStatement )
        {
            Trace( whileStatement, $"Emitting while statement: '{whileStatement}'" );

            // Create labels
            var conditionLabel = CreateLabel( "WhileConditionLabel" );
            var endLabel = CreateLabel( "WhileEndLabel" );

            // Emit condition check
            {
                ResolveLabel( conditionLabel );

                // compile condition expression, which should push a boolean value to the stack
                if ( !TryEmitExpression( whileStatement.Condition, false ) )
                {
                    Error( whileStatement.Condition, "Failed to emit while statement condition" );
                    return false;
                }

                // Jump to the end of the loop if condition is NOT true
                Emit( FlowScriptInstruction.IF( endLabel.Index ) );
            }

            // Emit body
            {
                // Enter while body scope
                PushScope();

                // allow break & continue
                Scope.BreakLabel = endLabel;
                Scope.ContinueLabel = conditionLabel;

                // emit body
                Trace( whileStatement.Body, "Emitting while statement body" );
                if ( !TryEmitCompoundStatement( whileStatement.Body ) )
                {
                    Error( whileStatement.Body, "Failed to emit while statement body" );
                    return false;
                }

                // jump to condition check
                Emit( FlowScriptInstruction.GOTO( conditionLabel.Index ) );

                // Exit while body scope
                PopScope();
            }

            // We're at the end of the while loop
            ResolveLabel( endLabel );

            return true;
        }

        //
        // Switch statement
        //
        private bool TryEmitSwitchStatement( FlowScriptSwitchStatement switchStatement )
        {
            Trace( switchStatement, $"Emitting switch statement: '{switchStatement}'" );
            PushScope();

            var defaultLabel = switchStatement.Labels.SingleOrDefault( x => x is FlowScriptDefaultSwitchLabel );
            if ( switchStatement.Labels.Last() != defaultLabel )
            {
                switchStatement.Labels.Remove( defaultLabel );
                switchStatement.Labels.Add( defaultLabel );
            }

            var switchEndLabel = CreateLabel( "SwitchStatementEndLabel" );
            var labelBodyLabels = new List< Label >();
            foreach ( var label in switchStatement.Labels )
            {
                if ( label is FlowScriptConditionSwitchLabel conditionLabel )
                {
                    // Emit condition expression, which should push a boolean value to the stack
                    if ( !TryEmitExpression( conditionLabel.Condition, false ) )
                    {
                        Error( conditionLabel.Condition, "Failed to emit switch statement label condition" );
                        return false;
                    }

                    // emit switch on expression
                    if ( !TryEmitExpression( switchStatement.SwitchOn, false ) )
                    {
                        Error( switchStatement.SwitchOn, "Failed to emit switch statement condition" );
                        return false;
                    }

                    // emit equality check, but check if it's not equal to jump to the body if it is
                    Emit( FlowScriptInstruction.NEQ() );

                    // generate label for jump if condition is false
                    var labelBodyLabel = CreateLabel( "SwitchStatementLabelBodyLabel" );

                    // emit if instruction that jumps to the body if the condition is met
                    Emit( FlowScriptInstruction.IF( labelBodyLabel.Index ) );

                    labelBodyLabels.Add( labelBodyLabel );
                }
            }

            if ( defaultLabel != null )
            {
                // Emit body of default case first
                Scope.BreakLabel = switchEndLabel;

                // Emit default case body
                Trace( "Compiling switch statement label body" );
                if ( !TryEmitStatements( defaultLabel.Body ) )
                {
                    Error( "Failed to compile switch statement label body" );
                    return false;
                }
            }

            // Emit other label bodies
            for ( var i = 0; i < switchStatement.Labels.Count; i++ )
            {
                var label = switchStatement.Labels[ i ];

                if ( label is FlowScriptConditionSwitchLabel )
                {
                    // Resolve body label
                    var labelBodyLabel = labelBodyLabels[ i ];
                    ResolveLabel( labelBodyLabel );

                    // Break jumps to end of switch
                    Scope.BreakLabel = switchEndLabel;

                    // Emit body
                    Trace( "Compiling switch statement label body" );
                    if ( !TryEmitStatements( label.Body ) )
                    {
                        Error( "Failed to compile switch statement label body" );
                        return false;
                    }
                }
            }

            ResolveLabel( switchEndLabel );

            PopScope();
            return true;
        }

        //
        // Control statements
        //
        private bool TryEmitBreakStatement( FlowScriptBreakStatement breakStatement )
        {
            if ( !Scope.TryGetBreakLabel( out var label ) )
            {
                Error( breakStatement, "Break statement is invalid in this context" );
                return false;
            }

            Emit( FlowScriptInstruction.GOTO( label.Index ) );

            return true;
        }

        private bool TryEmitContinueStatement( FlowScriptContinueStatement continueStatement )
        {
            if ( !Scope.TryGetContinueLabel( out var label ) )
            {
                Error( continueStatement, "Continue statement is invalid in this context" );
                return false;
            }

            Emit( FlowScriptInstruction.GOTO( label.Index ) );

            return true;
        }

        private bool TryEmitReturnStatement( FlowScriptReturnStatement returnStatement )
        {
            Trace( returnStatement, $"Emitting return statement: '{returnStatement}'" );

            if ( EnableStackCookie )
            {
                // Check stack cookie
                Emit( FlowScriptInstruction.PUSHI( mProcedureDeclaration.Identifier.Text.GetHashCode() ) );
                Emit( FlowScriptInstruction.NEQ() );
                var label = CreateLabel( "IfStackCookieIsValid" );
                Emit( FlowScriptInstruction.IF( label.Index ) );
                EmitTracePrint( "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!", false );
                EmitTracePrint( "!!! Error: Stack cookie is invalid !!!!", false );
                EmitTracePrint( "!!! This is likely a compiler bug! !!!!", false );
                EmitTracePrint( "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!", false );
                ResolveLabel( label );
            }

            if ( EnableProcedureTracing )
            {
                TraceProcedureReturn();
            }

            // Save return address in a temporary variable
            if ( returnStatement.Value != null )
            {
                if ( mProcedureDeclaration.ReturnType.ValueType == FlowScriptValueType.Void )
                {
                    Error( returnStatement, "Procedure with void return type can't return a value" );
                    return false;
                }

                // Emit return value
                if ( !TryEmitExpression( returnStatement.Value, false ) )
                {
                    Error( returnStatement.Value, $"Failed to emit return value: {returnStatement.Value}" );
                    return false;
                }

                if ( sTypeToBaseTypeMap[mProcedureDeclaration.ReturnType.ValueType] == FlowScriptValueType.Int )
                    Emit( FlowScriptInstruction.POPLIX( mIntReturnValueVariable.Index ) );
                else
                    Emit( FlowScriptInstruction.POPLFX( mFloatReturnValueVariable.Index ) );
            }
            else if ( mProcedureDeclaration.ReturnType.ValueType != FlowScriptValueType.Void )
            {
                Error( returnStatement, "Missing return statement value for procedure with non-void return type" );
                return false;
            }

            // emit end
            Emit( FlowScriptInstruction.END() );
            return true;
        }

        private bool TryEmitGotoStatement( FlowScriptGotoStatement gotoStatement )
        {
            Trace( gotoStatement, $"Emitting goto statement: '{gotoStatement}'" );

            if ( !mLabels.TryGetValue( gotoStatement.LabelIdentifier.Text, out var label ) )
            {
                Error( gotoStatement.LabelIdentifier, $"Goto statement referenced undeclared label: {gotoStatement.LabelIdentifier}" );
                return false;
            }

            // emit goto
            Emit( FlowScriptInstruction.GOTO( label.Index ) );
            return true;
        }

        //
        // Helpers
        //
        private void TraceFunctionCall( FlowScriptFunctionDeclaration declaration )
        {
            EmitTracePrint( $"Call to function '{ declaration.Identifier }'" );
            if ( declaration.Parameters.Count > 0 )
            {
                EmitTracePrint( "Arguments:" );
                var saves = new Stack< Variable >();

                foreach ( var parameter in declaration.Parameters )
                {
                    switch ( parameter.Type.ValueType )
                    {
                        case FlowScriptValueType.Int:
                            if ( mPUTFunctionIndex != -1 )
                                saves.Push( EmitTracePrintIntegerNoPush() );
                            break;
                        case FlowScriptValueType.Float:
                            if ( mPUTFFunctionIndex != -1 )
                                saves.Push( EmitTracePrintFloatNoPush() );
                            break;
                        case FlowScriptValueType.Bool:
                            if ( mPUTSFunctionIndex != -1 )
                                saves.Push( EmitTracePrintBoolNoPush() );
                            break;
                        case FlowScriptValueType.String:
                            //saves.Push( EmitTracePrintStringNoPush() );
                            break;
                    }
                }

                // Push values back onto stack
                while ( saves.Count > 0 )
                {
                    var variable = saves.Pop();
                    switch ( variable.Declaration.Type.ValueType )
                    {
                        case FlowScriptValueType.Bool:
                            if ( mPUTSFunctionIndex != -1 )
                                EmitUnchecked( FlowScriptInstruction.PUSHLIX( variable.Index ) );
                            break;
                        case FlowScriptValueType.Int:
                            if ( mPUTFunctionIndex != -1 )
                                EmitUnchecked( FlowScriptInstruction.PUSHLIX( variable.Index ) );
                            break;
                        case FlowScriptValueType.Float:
                            if ( mPUTFFunctionIndex != -1 )
                                EmitUnchecked( FlowScriptInstruction.PUSHLFX( variable.Index ) );
                            break;
                    }
                }
            }
        }

        private Variable EmitTracePrintStringNoPush()
        {
            var save = Scope.GenerateVariable( FlowScriptValueType.String, mNextIntVariableIndex++ );

            // Pop integer value off stack and save it in a temporary variable
            EmitUnchecked( FlowScriptInstruction.POPLFX( save.Index ) );

            // Print it to log
            EmitUnchecked( FlowScriptInstruction.PUSHLFX( save.Index ) );
            EmitUnchecked( FlowScriptInstruction.COMM( (short)mPUTSFunctionIndex ) );

            return save;
        }

        private void TraceFunctionCallReturnValue( FlowScriptFunctionDeclaration declaration )
        {
            EmitTracePrint( $"Call to function '{ declaration.Identifier }' returned:" );
            EmitTracePrintValue( declaration.ReturnType.ValueType );
        }

        private void TraceProcedureCall( FlowScriptProcedureDeclaration declaration )
        {
            EmitTracePrint( $"Call to procedure '{ declaration.Identifier }'" );

            if ( declaration.Parameters.Count > 0 )
            {
                EmitTracePrint( "Arguments:" );

                int intParameterCount = 1;
                int floatParameterCount = 1;

                foreach ( var parameter in declaration.Parameters )
                {
                    if ( parameter.Type.ValueType == FlowScriptValueType.Int && mPUTFunctionIndex != -1 )
                    {
                        Emit( FlowScriptInstruction.PUSHLIX( ( short )( mNextIntParameterVariableIndex + intParameterCount ) ) );
                    }
                    if ( parameter.Type.ValueType == FlowScriptValueType.Bool && mPUTSFunctionIndex != -1 )
                    {
                        Emit( FlowScriptInstruction.PUSHLIX( ( short )( mNextIntParameterVariableIndex + intParameterCount ) ) );
                    }
                    else if ( mPUTFFunctionIndex != -1 )
                    {
                        Emit( FlowScriptInstruction.PUSHLFX( ( short )( mNextFloatParameterVariableIndex + floatParameterCount ) ) );
                    }

                    EmitTracePrintValue( parameter.Type.ValueType );

                    if ( parameter.Type.ValueType == FlowScriptValueType.Int && mPUTFunctionIndex != -1 )
                    {
                        Emit( FlowScriptInstruction.POPLIX( ( short )( mNextIntParameterVariableIndex + intParameterCount ) ) );
                        ++intParameterCount;
                    }
                    else if ( parameter.Type.ValueType == FlowScriptValueType.Bool && mPUTSFunctionIndex != -1 )
                    {
                        Emit( FlowScriptInstruction.POPLIX( ( short )( mNextIntParameterVariableIndex + intParameterCount ) ) );
                        ++intParameterCount;
                    }
                    else if( mPUTFFunctionIndex != -1 )
                    {
                        Emit( FlowScriptInstruction.POPLFX( ( short )( mNextFloatParameterVariableIndex + floatParameterCount ) ) );
                        ++floatParameterCount;
                    }
                }
            }
        }

        private void TraceProcedureCallReturnValue( FlowScriptProcedureDeclaration declaration )
        {
            EmitTracePrint( $"Call to procedure '{ declaration.Identifier }' returned:" );
            EmitTracePrintValue( declaration.ReturnType.ValueType );
        }

        private void TraceProcedureStart()
        {
            EmitTracePrint( $"Entered procedure: '{ mProcedureDeclaration.Identifier.Text }'" );
        }

        private void TraceProcedureReturn()
        {
            EmitTracePrint( $"Exiting procedure: '{ mProcedureDeclaration.Identifier.Text }'" );
        }

        private void Emit( FlowScriptInstruction instruction )
        {
            // Emit instruction
            mInstructions.Add( instruction );
            TraceInstructionStackBehaviour( instruction );
        }

        private void TraceInstructionStackBehaviour( FlowScriptInstruction instruction )
        {
            switch ( instruction.Opcode )
            {
                case FlowScriptOpcode.PUSHI:
                case FlowScriptOpcode.PUSHF:
                case FlowScriptOpcode.PUSHIX:
                case FlowScriptOpcode.PUSHIF:
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.PUSHREG:
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.POPIX:
                case FlowScriptOpcode.POPFX:
                    --mStackValueCount;
                    break;
                case FlowScriptOpcode.END:
                {
                    // Log stack value count at procedure end
                    mLogger.Debug( $"{mStackValueCount} values on stack at END" );

                    if ( mStackValueCount < 1 )
                    {
                        mLogger.Error( "Stack underflow!!!" );
                    }
                    else if ( mStackValueCount != 1 )
                    {
                        mLogger.Error( "Return address corruption" );
                    }
                }
                    break;
                case FlowScriptOpcode.ADD:
                case FlowScriptOpcode.SUB:
                case FlowScriptOpcode.MUL:
                case FlowScriptOpcode.DIV:
                    mStackValueCount -= 2;
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.EQ:
                case FlowScriptOpcode.NEQ:
                case FlowScriptOpcode.S:
                case FlowScriptOpcode.L:
                case FlowScriptOpcode.SE:
                case FlowScriptOpcode.LE:
                case FlowScriptOpcode.IF:
                    mStackValueCount -= 2;
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.PUSHIS:
                case FlowScriptOpcode.PUSHLIX:
                case FlowScriptOpcode.PUSHLFX:
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.POPLIX:
                case FlowScriptOpcode.POPLFX:
                    --mStackValueCount;
                    break;
                case FlowScriptOpcode.PUSHSTR:
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.CALL:
                    break;
                case FlowScriptOpcode.COMM:
                {
                    var functionCalled = mRootScope.Functions.Values.Single( x => x.Index == instruction.Operand.GetInt16Value() );
                    mStackValueCount -= functionCalled.Declaration.Parameters.Count;
                }
                    break;
                case FlowScriptOpcode.OR:
                    mStackValueCount -= 2;
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.PROC:
                    break;
                case FlowScriptOpcode.JUMP:
                    break;
                case FlowScriptOpcode.RUN:
                    break;
                case FlowScriptOpcode.GOTO:
                    break;
                case FlowScriptOpcode.MINUS:
                    --mStackValueCount;
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.NOT:
                    --mStackValueCount;
                    ++mStackValueCount;
                    break;
                case FlowScriptOpcode.AND:
                    mStackValueCount -= 2;
                    ++mStackValueCount;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void EmitTracePrint( string message, bool prefixTrace = true )
        {
            if ( mPUTSFunctionIndex == -1 )
                return;

            var messageFormatted = message;
            if ( prefixTrace )
                messageFormatted = $"Trace: {message}";

            EmitUnchecked( FlowScriptInstruction.PUSHSTR( messageFormatted ) );
            EmitUnchecked( FlowScriptInstruction.COMM( (short)mPUTSFunctionIndex ) );
        }

        private void EmitTracePrintValue( FlowScriptValueType type )
        {
            switch ( type )
            {
                case FlowScriptValueType.Int:
                    EmitTracePrintInteger();
                    break;
                case FlowScriptValueType.Float:
                    EmitTracePrintFloat();
                    break;
                case FlowScriptValueType.Bool:
                    EmitTracePrintBool();
                    break;
            }
        }

        private void EmitTracePrintInteger()
        {
            if ( mPUTFunctionIndex == -1 )
                return;

            var save = EmitTracePrintIntegerNoPush();

            // Push the value back to the stack
            EmitUnchecked( FlowScriptInstruction.PUSHLIX( save.Index ) );
        }

        private Variable EmitTracePrintIntegerNoPush()
        {
            System.Diagnostics.Trace.Assert( mPUTFunctionIndex != -1 );
            var save = Scope.GenerateVariable( FlowScriptValueType.Int, mNextIntVariableIndex++ );

            // Pop integer value off stack and save it in a temporary variable
            EmitUnchecked( FlowScriptInstruction.POPLIX( save.Index ) );

            // Print it to log
            EmitUnchecked( FlowScriptInstruction.PUSHLIX( save.Index ) );
            EmitUnchecked( FlowScriptInstruction.COMM( (short)mPUTFunctionIndex ) );

            return save;
        }

        private void EmitTracePrintFloat()
        {
            if ( mPUTFFunctionIndex == -1 )
                return;

            var save = EmitTracePrintFloatNoPush();

            // Push the value back to the stack
            EmitUnchecked( FlowScriptInstruction.PUSHLFX( save.Index ) );
        }

        private Variable EmitTracePrintFloatNoPush()
        {
            System.Diagnostics.Trace.Assert( mPUTFFunctionIndex != -1 );
            var save = Scope.GenerateVariable( FlowScriptValueType.Float, mNextFloatVariableIndex++ );

            // Pop integer value off stack and save it in a temporary variable
            EmitUnchecked( FlowScriptInstruction.POPLFX( save.Index ) );

            // Print it to log
            EmitUnchecked( FlowScriptInstruction.PUSHLFX( save.Index ) );
            EmitUnchecked( FlowScriptInstruction.COMM( (short)mPUTFFunctionIndex ) );

            return save;
        }

        private void EmitTracePrintBool()
        {
            if ( mPUTSFunctionIndex == -1 )
                return;

            var save = EmitTracePrintBoolNoPush();

            // Push the value back to the stack
            EmitUnchecked( FlowScriptInstruction.PUSHLIX( save.Index ) );
        }

        private Variable EmitTracePrintBoolNoPush()
        {
            System.Diagnostics.Trace.Assert( mPUTSFunctionIndex != -1 );

            var save = Scope.GenerateVariable( FlowScriptValueType.Int, mNextIntVariableIndex++ );

            // Pop integer value off stack and save it in a temporary variable
            EmitUnchecked( FlowScriptInstruction.POPLIX( save.Index ) );

            // Print it to log
            var elseLabel = CreateLabel( "IfElseLabel" );
            var endLabel = CreateLabel( "IfEndLabel" );

            // if ( x == 1 )
            EmitUnchecked( FlowScriptInstruction.PUSHIS( 1 ) );
            EmitUnchecked( FlowScriptInstruction.PUSHLIX( save.Index ) );
            EmitUnchecked( FlowScriptInstruction.EQ() );
            EmitUnchecked( FlowScriptInstruction.IF( elseLabel.Index ) );
            {
                // PUTS( "true" );
                EmitTracePrint( "true" );
                EmitUnchecked( FlowScriptInstruction.GOTO( endLabel.Index ) );
            }
            // else
            ResolveLabel( elseLabel );
            {
                // PUTS( "false" );
                EmitTracePrint( "false" );
            }
            ResolveLabel( endLabel );

            return save;
        }

        private void EmitUnchecked( FlowScriptInstruction instruction )
        {
            mInstructions.Add( instruction );
        }

        private Label CreateLabel( string name )
        {
            var label = new Label();
            label.Index = ( short )mLabels.Count;
            label.Name = name + "_" + mNextLabelIndex++;

            mLabels.Add( label.Name, label );

            return label;
        }

        private void ResolveLabel( Label label )
        {
            label.InstructionIndex = ( short )( mInstructions.Count );
            label.IsResolved = true;

            Trace( $"Resolved label {label.Name} to instruction index {label.InstructionIndex}" );
        }

        private void PushScope()
        {
            mScopeStack.Push( new ScopeContext( mScopeStack.Peek() ) );
            Trace( "Entered scope" );
        }

        private void PopScope()
        {
            //mNextIntVariableIndex -= ( short )Scope.Variables.Count( x => sTypeToBaseTypeMap[x.Value.Declaration.Type.ValueType] == FlowScriptValueType.Int );
            //mNextFloatVariableIndex -= ( short )Scope.Variables.Count( x => sTypeToBaseTypeMap[x.Value.Declaration.Type.ValueType] == FlowScriptValueType.Float );
            mScopeStack.Pop();
            Trace( "Exited scope" );
        }

        //
        // Logging
        //
        private void Trace( FlowScriptSyntaxNode node, string message )
        {
            if ( node.SourceInfo != null )
                Trace( $"({node.SourceInfo.Line:D4}:{node.SourceInfo.Column:D4}) {message}" );
            else
                Trace( message );
        }

        private void Trace( string message )
        {
            mLogger.Trace( $"{message}" );
        }

        private void Info( FlowScriptSyntaxNode node, string message )
        {
            if ( node.SourceInfo != null )
                Info( $"({node.SourceInfo.Line:D4}:{node.SourceInfo.Column:D4}) {message}" );
            else
                Info( message );
        }

        private void Info( string message )
        {
            mLogger.Info( $"{message}" );
        }

        private void Error( FlowScriptSyntaxNode node, string message )
        {
            if ( node.SourceInfo != null )
                Error( $"({node.SourceInfo.Line:D4}:{node.SourceInfo.Column:D4}) {message}" );
            else
                Error( message );

            if ( Debugger.IsAttached )
                Debugger.Break();
        }

        private void Error( string message )
        {
            mLogger.Error( $"{message}" );
        }

        private void Warning( FlowScriptSyntaxNode node, string message )
        {
            if ( node.SourceInfo != null )
                Warning( $"({node.SourceInfo.Line:D4}:{node.SourceInfo.Column:D4}) {message}" );
            else
                Warning( message );
        }

        private void Warning( string message )
        {
            mLogger.Warning( $"{message}" );
        }

        private class Function
        {
            public FlowScriptFunctionDeclaration Declaration { get; set; }

            public short Index { get; set; }
        }

        private class Procedure
        {
            public FlowScriptProcedureDeclaration Declaration { get; set; }

            public short Index { get; set; }
        }

        private class Variable
        {
            public FlowScriptVariableDeclaration Declaration { get; set; }

            public short Index { get; set; }
        }

        private class Label
        {
            public string Name { get; set; }

            public short Index { get; set; }

            public short InstructionIndex { get; set; }

            public bool IsResolved { get; set; }
        }

        private class Enum
        {
            public FlowScriptEnumDeclaration Declaration { get; set; }

            public Dictionary<string, FlowScriptExpression> Members { get; set; }
        }

        private class ScopeContext
        {
            public ScopeContext Parent { get; }

            public Dictionary<string, Function> Functions { get; }

            public Dictionary<string, Procedure> Procedures { get; }

            public Dictionary<string, Variable> Variables { get; }

            public Dictionary<string, Enum> Enums { get; }

            public Label BreakLabel { get; set; }

            public Label ContinueLabel { get; set; }

            public ScopeContext( ScopeContext parent )
            {
                Parent = parent;
                Functions = new Dictionary<string, Function>();
                Procedures = new Dictionary<string, Procedure>();
                Variables = new Dictionary<string, Variable>();
                Enums = new Dictionary<string, Enum>();
            }

            public bool TryGetBreakLabel( out Label label )
            {
                if ( BreakLabel != null )
                {
                    label = BreakLabel;
                    return true;
                }

                if ( Parent != null )
                    return Parent.TryGetBreakLabel( out label );

                label = null;
                return false;
            }

            public bool TryGetContinueLabel( out Label label )
            {
                if ( ContinueLabel != null )
                {
                    label = ContinueLabel;
                    return true;
                }

                if ( Parent != null )
                    return Parent.TryGetContinueLabel( out label );

                label = null;
                return false;
            }

            public bool TryGetFunction( string name, out Function function )
            {
                if ( !Functions.TryGetValue( name, out function ) )
                {
                    if ( Parent == null )
                        return false;

                    if ( !Parent.TryGetFunction( name, out function ) )
                        return false;
                }

                return true;
            }

            public bool TryGetProcedure( string name, out Procedure procedure )
            {
                if ( !Procedures.TryGetValue( name, out procedure ) )
                {
                    if ( Parent == null )
                        return false;

                    if ( !Parent.TryGetProcedure( name, out procedure ) )
                        return false;
                }

                return true;
            }

            public bool TryGetVariable( string name, out Variable variable )
            {
                if ( !Variables.TryGetValue( name, out variable ) )
                {
                    if ( Parent == null )
                        return false;

                    if ( !Parent.TryGetVariable( name, out variable ) )
                        return false;
                }

                return true;
            }

            public bool TryGetEnum( string name, out Enum enumDeclaration )
            {
                if ( !Enums.TryGetValue( name, out enumDeclaration ) )
                {
                    if ( Parent == null )
                        return false;

                    if ( !Parent.TryGetEnum( name, out enumDeclaration ) )
                        return false;
                }

                return true;
            }

            public bool TryDeclareFunction( FlowScriptFunctionDeclaration declaration )
            {
                if ( TryGetFunction( declaration.Identifier.Text, out _ ) )
                    return false;

                var function = new Function();
                function.Declaration = declaration;
                function.Index = ( short )declaration.Index.Value;

                Functions[declaration.Identifier.Text] = function;

                return true;
            }

            public bool TryDeclareProcedure( FlowScriptProcedureDeclaration declaration )
            {
                if ( TryGetProcedure( declaration.Identifier.Text, out _ ) )
                    return false;

                var procedure = new Procedure();
                procedure.Declaration = declaration;
                procedure.Index = ( short )Procedures.Count;

                Procedures[declaration.Identifier.Text] = procedure;

                return true;
            }

            public bool TryDeclareVariable( FlowScriptVariableDeclaration declaration )
            {
                return TryDeclareVariable( declaration, -1 );
            }

            public bool TryDeclareVariable( FlowScriptVariableDeclaration declaration, short index )
            {
                if ( TryGetVariable( declaration.Identifier.Text, out _ ) )
                    return false;

                var variable = new Variable();
                variable.Declaration = declaration;
                variable.Index = index;

                Variables[declaration.Identifier.Text] = variable;

                return true;
            }

            public bool TryDeclareEnum( FlowScriptEnumDeclaration declaration )
            {
                if ( TryGetEnum( declaration.Identifier.Text, out _ ) )
                    return false;

                var enumType = new Enum
                {
                    Declaration = declaration,
                    Members = declaration.Values.ToDictionary( x => x.Identifier.Text, y => y.Value )
                };

                int nextMemberValue = 0;
                bool anyImplicitValues = false;

                for ( int i = 0; i < enumType.Members.Count; i++ )
                {
                    var key = enumType.Members.Keys.ElementAt( i );
                    var value = enumType.Members[key];

                    if ( value == null )
                    {
                        enumType.Members[key] = new FlowScriptIntLiteral( nextMemberValue++ );
                        anyImplicitValues = true;
                    }
                    else
                    {
                        if ( !TryGetNextMemberValue( enumType.Members, value, out nextMemberValue ) )
                        {
                            // Only error if there are any implicit values
                            if ( anyImplicitValues )
                                return false;
                        }
                    }
                }

                Enums[declaration.Identifier.Text] = enumType;

                return true;
            }

            private bool TryGetNextMemberValue( Dictionary<string, FlowScriptExpression> members, FlowScriptExpression enumValue, out int nextMemberValue )
            {
                if ( enumValue is FlowScriptIntLiteral intLiteral )
                {
                    nextMemberValue = intLiteral.Value + 1;
                    return true;
                }
                else if ( enumValue is FlowScriptIdentifier identifier )
                {
                    if ( members.TryGetValue( identifier.Text, out var value ) )
                    {
                        if ( !TryGetNextMemberValue( members, value, out nextMemberValue ) )
                            return false;
                    }
                }

                nextMemberValue = -1;
                return false;
            }

            public Variable GenerateVariable( FlowScriptValueType type, short index )
            {
                var declaration = new FlowScriptVariableDeclaration(
                    new FlowScriptVariableModifier( FlowScriptModifierType.Local ),
                    new FlowScriptTypeIdentifier( type ),
                    new FlowScriptIdentifier( type, $"<>__CompilerGenerated{type}Variable{index}" ),
                    null );

                bool result;
                result = TryDeclareVariable( declaration, index );
                Debug.Assert( result );

                result = TryGetVariable( declaration.Identifier.Text, out var variable );
                Debug.Assert( result );

                return variable;
            }
        }
    }
}
