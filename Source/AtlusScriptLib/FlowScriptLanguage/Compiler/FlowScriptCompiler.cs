﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using AtlusScriptLib.Common.Logging;
using AtlusScriptLib.FlowScriptLanguage.Compiler.Processing;
using AtlusScriptLib.FlowScriptLanguage.Syntax;
using MoreLinq;

namespace AtlusScriptLib.FlowScriptLanguage.Compiler
{
    public class FlowScriptCompiler
    {
        // logger
        private Logger mLogger;

        // compiler-level state
        private FlowScriptFormatVersion mFormatVersion;
        private FlowScript mScript;
        private int mNextLabelIndex;
        private Stack<Scope> mScopeStack;
        private Scope mRootScope;
        private short mNextIntVariableIndex;
        private short mNextFloatVariableIndex;

        // procedure-level state
        private List<FlowScriptInstruction> mInstructions;
        private Dictionary<string, Label> mLabels;

        private Scope CurrentScope => mScopeStack.Peek();

        public FlowScriptCompiler( FlowScriptFormatVersion version )
        {
            mLogger = new Logger( nameof( FlowScriptCompiler ) );
            mFormatVersion = version;
        }

        /// <summary>
        /// Adds a compiler log listener. Use this if you want to see what went wrong during compilation.
        /// </summary>
        /// <param name="listener">The listener to add.</param>
        public void AddListener( LogListener listener )
        {
            listener.Subscribe( mLogger );
        }

        public bool TryCompile( FlowScriptCompilationUnit compilationUnit, out FlowScript flowScript )
        {
            LogInfo( compilationUnit, $"Start compiling FlowScript with version {mFormatVersion}" );

            flowScript = null;

            if ( !TryCompileCompilationUnit( compilationUnit ))
                return false;

            flowScript = mScript;

            return true;
        }

        private void InitializeCompilationState()
        {
            // todo: imports?
            mScript = new FlowScript( mFormatVersion );
            mNextLabelIndex = 0;
            mScopeStack = new Stack<Scope>();
            mRootScope = new Scope( null );
            mScopeStack.Push( mRootScope );
        }

        private void PushScope()
        {
            mScopeStack.Push( new Scope( mScopeStack.Peek() ) );
            mLogger.Info( "Entered scope" );
        }

        private void PopScope()
        {
            mScopeStack.Pop();
            mLogger.Info( "Exited scope" );
        }

        private bool TryCompileCompilationUnit( FlowScriptCompilationUnit compilationUnit )
        {
            InitializeCompilationState();
            if ( !TryRegisterFunctionsAndProcedures( compilationUnit ) )
                return false;

            foreach ( var statement in compilationUnit.Statements )
            {
                if ( statement is FlowScriptProcedureDeclaration procedureDeclaration )
                {
                    if ( procedureDeclaration.Body != null )
                    {
                        if ( !TryCompileProcedureDeclaration( procedureDeclaration, out var procedure ) )
                            return false;

                        mScript.Procedures.Add( procedure );
                    }
                }
                else if ( !(statement is FlowScriptFunctionDeclaration) )
                {
                    LogError( statement, "Unexpected top-level statement type" );
                    return false;
                }
            }

            return true;
        }

        private bool TryRegisterFunctionsAndProcedures( FlowScriptCompilationUnit compilationUnit )
        {
            mLogger.Info( "Registering functions and procedures" );

            // top-level only
            foreach ( var statement in compilationUnit.Statements )
            {
                if ( statement is FlowScriptFunctionDeclaration functionDeclaration )
                {
                    if ( !CurrentScope.TryDeclareFunction( functionDeclaration ) )
                    {
                        LogError( functionDeclaration, $"Failed to register function: {functionDeclaration}" );
                        return false;
                    }
                }
                else if ( statement is FlowScriptProcedureDeclaration procedureDeclaration )
                {
                    if ( !CurrentScope.TryDeclareProcedure( procedureDeclaration ) )
                    {
                        LogError( procedureDeclaration, $"Failed to register procedure: {procedureDeclaration}" );
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryCompileProcedureDeclaration( FlowScriptProcedureDeclaration procedureDeclaration, out FlowScriptProcedure procedure )
        {
            LogInfo( procedureDeclaration, $"Compiling procedure declaration: {procedureDeclaration}" );

            // Compile procedure body
            mInstructions = new List<FlowScriptInstruction>();
            mLabels = new Dictionary<string, Label>();

            // Emit procedure start         
            Emit( FlowScriptInstruction.PROC( mRootScope.Procedures[procedureDeclaration.Identifier.Text].Index ) );
            PushScope();

            // To mimick the official compiler
            mNextLabelIndex++;

            // Register labels in procedure body before codegen
            if ( !TryRegisterLabels(procedureDeclaration.Body) )
            {
                procedure = null;
                LogError( procedureDeclaration.Body, "Failed to register labels in procedure body" );
                return false;
            }

            // Emit procedure parameters
            foreach ( var parameter in procedureDeclaration.Parameters )
            {
                LogInfo( parameter, $"Compiling parameter: {parameter}" );

                var parameterDeclaration = new FlowScriptVariableDeclaration(
                            new List<FlowScriptVariableModifier>() { new FlowScriptVariableModifier() },
                            parameter.TypeIdentifier,
                            parameter.Identifier,
                            null );

                if ( !TryCompileVariableDeclaration( parameterDeclaration ) )
                {
                    procedure = null;
                    return false;
                }

                if ( !CurrentScope.TryGetVariable( parameter.Identifier.Text, out var parameterVariable ) )
                {
                    procedure = null;
                    return false;
                }

                if ( parameter.TypeIdentifier.ValueType != FlowScriptValueType.Float )
                    Emit( FlowScriptInstruction.POPLIX( parameterVariable.Index ) );
                else
                    Emit( FlowScriptInstruction.POPLFX( parameterVariable.Index ) );
            }

            // Emit procedure body
            LogInfo( procedureDeclaration.Body, "Compiling procedure body" );
            if ( !TryCompileCompoundStatement( procedureDeclaration.Body ) )
            {
                procedure = null;
                LogError( procedureDeclaration.Body, "Failed to compile procedure body" );
                return false;
            }

            // Emit procedure end
            if ( !(procedureDeclaration.Body.Last() is FlowScriptReturnStatement ) )
                Emit( FlowScriptInstruction.END() );

            PopScope();

            // Create labels
            mLogger.Info( "Resolving labels" );
            if ( mLabels.Values.Any(x => !x.IsResolved) )
            {
                foreach ( var item in mLabels.Values.Where( x => !x.IsResolved ))
                    mLogger.Error( $"Label '{item.Name}' is referenced but not declared" );

                procedure = null;
                mLogger.Error( "Failed to compile procedure because one or more undeclared labels are referenced" );
                return false;
            }

            var labels = mLabels.Values.Select( x => new FlowScriptLabel( x.Name, x.InstructionIndex ) ).ToList();
            mLabels.Clear();

            // Create the procedure object
            procedure = new FlowScriptProcedure( procedureDeclaration.Identifier.Text, mInstructions, labels );

            mLogger.Info( $"Done compiling procedure declaration: {procedureDeclaration}" );

            return true;
        }

        private bool TryRegisterLabels( FlowScriptCompoundStatement body )
        {
            foreach ( var declaration in body.Select( x => x as FlowScriptDeclaration ).Where( x => x != null ) )
            {
                if ( declaration.DeclarationType == FlowScriptDeclarationType.Label )
                {
                    mLabels[declaration.Identifier.Text] = DeclareLabel( declaration.Identifier.Text );
                }
            }

            return true;
        }

        private bool TryCompileStatements( IEnumerable<FlowScriptStatement> statements )
        {
            foreach ( var statement in statements )
            {
                if ( !TryCompileStatement( statement ) )
                    return false;
            }

            return true;
        }

        private bool TryCompileCompoundStatement( FlowScriptCompoundStatement compoundStatement )
        {
            if ( !TryCompileStatements( compoundStatement ) )
                return false;

            return true;
        }

        private bool TryCompileStatement( FlowScriptStatement statement )
        {
            if ( statement is FlowScriptCompoundStatement compoundStatement )
            {
                if ( !TryCompileCompoundStatement( compoundStatement ) )
                    return false;
            }
            else if ( statement is FlowScriptDeclaration declaration )
            {
                if ( statement is FlowScriptVariableDeclaration variableDeclaration )
                {
                    if ( !TryCompileVariableDeclaration( variableDeclaration ) )
                        return false;
                }
                else if ( statement is FlowScriptLabelDeclaration labelDeclaration )
                {
                    if ( !TryCompileLabelDeclaration( labelDeclaration ) )
                        return false;
                }
                else
                {
                    LogError( statement, "Expected variable or label declaration" );
                    return false;
                }
            }
            else if ( statement is FlowScriptExpression expression )
            {
                if ( !TryCompileExpression( expression ) )
                    return false;
            }
            else if ( statement is FlowScriptIfStatement ifStatement )
            {
                if ( !TryCompileIfStatement( ifStatement ) )
                    return false;
            }
            else if ( statement is FlowScriptBreakStatement breakStatement )
            {
                if ( !TryCompileBreakStatement( breakStatement ) )
                    return false;
            }
            else if ( statement is FlowScriptReturnStatement returnStatement )
            {
                if ( !TryCompileReturnStatement( returnStatement ) )
                {
                    LogError( returnStatement, $"Failed to compile return statement: {returnStatement}" );
                    return false;
                }
            }
            else if ( statement is FlowScriptGotoStatement gotoStatement )
            {
                if ( !TryCompileGotoStatement( gotoStatement ) )
                {
                    LogError( gotoStatement, $"Failed to compile goto statement: {gotoStatement}" );
                    return false;
                }
            }
            else
            {
                LogError( statement, $"Compiling statement '{statement}' not implemented" );
                return false;
            }

            return true;
        }

        private bool TryCompileBreakStatement( FlowScriptBreakStatement breakStatement )
        {
            if ( CurrentScope.BreakLabel == null )
            {
                LogError( breakStatement, "Break statement is invalid in this context" );
                return false;
            }

            Emit( FlowScriptInstruction.GOTO( CurrentScope.BreakLabel.Index ) );

            return true;
        }

        private bool TryCompileVariableDeclaration( FlowScriptVariableDeclaration declaration )
        {
            LogInfo( declaration, $"Compiling variable declaration: {declaration}" );

            // register variable
            short variableIndex;
            if ( declaration.Type.ValueType == FlowScriptValueType.Float )
            {
                variableIndex = mNextFloatVariableIndex++;
            }
            else
            {
                variableIndex = mNextIntVariableIndex++;
            }

            if ( !CurrentScope.TryDeclareVariable( declaration, variableIndex ) )
            {
                LogError( declaration, $"Variable '{declaration}' has already been declared" );
                return false;
            }

            // compile the initializer if it has one         
            if ( declaration.Initializer != null )
            {
                LogInfo( declaration.Initializer, "Compiling variable initializer" );

                if ( !TryCompileVariableAssignment(declaration.Identifier, declaration.Initializer) )
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryCompileVariableAssignment( FlowScriptIdentifier identifier, FlowScriptExpression expression )
        {
            if ( !TryCompileExpression( expression ) )
            {
                LogError( expression, "Failed to compile variable assignment" );
                return false;
            }

            if ( !CurrentScope.TryGetVariable( identifier.Text, out var variable ) )
            {
                LogError( identifier, $"Assignment to undeclared variable: {identifier}" );
                return false;
            }

            // load the value into the variable
            if ( variable.Declaration.Type.ValueType != FlowScriptValueType.Float )
                Emit( FlowScriptInstruction.POPLIX( variable.Index ) );
            else
                Emit( FlowScriptInstruction.POPLFX( variable.Index ) );

            return true;
        }

        private bool TryCompileLabelDeclaration( FlowScriptLabelDeclaration declaration )
        {
            LogInfo( declaration, $"Compiling label declaration: {declaration}" );

            // register label
            if ( !mLabels.TryGetValue(declaration.Identifier.Text, out var label ))
            {
                LogError( declaration.Identifier, $"Unexpected declaration of an undeclared label: '{declaration}'" );
                return false;
            }

            ResolveLabel( label );

            return true;
        }

        private bool TryCompileExpression( FlowScriptExpression expression)
        {
            if ( expression is FlowScriptCallOperator callExpression )
            {
                if ( !TryCompileFunctionOrProcedureCall( callExpression ) )
                    return false;
            }
            else if ( expression is FlowScriptUnaryExpression unaryExpression )
            {
                if ( !TryCompileUnaryExpression( unaryExpression ) )
                    return false;
            }
            else if ( expression is FlowScriptBinaryExpression binaryExpression )
            {
                if ( !TryCompileBinaryExpression( binaryExpression ) )
                    return false;
            }
            else if ( expression is FlowScriptIdentifier identifier )
            {
                if ( !TryCompilePushVariableValue( identifier ) )
                    return false;
            }
            else if ( expression is FlowScriptBoolLiteral boolLiteral )
            {
                EmitPushBoolLiteral( boolLiteral );
            }
            else if ( expression is FlowScriptIntLiteral intLiteral )
            {
                EmitPushIntLiteral( intLiteral );
            }
            else if ( expression is FlowScriptFloatLiteral floatLiteral )
            {
                EmitPushFloatLiteral( floatLiteral );
            }
            else if ( expression is FlowScriptStringLiteral stringLiteral )
            {
                EmitPushStringLiteral( stringLiteral );
            }
            else
            {
                LogError( expression, $"Compiling expression '{expression}' not implemented" );
                return false;
            }

            return true;
        }

        private bool TryCompileFunctionOrProcedureCall( FlowScriptCallOperator callExpression)
        {
            LogInfo( callExpression, $"Compiling function or procedure call: {callExpression}" );

            // Compile expressions backwards so they are pushed to the stack in the right order
            mLogger.Info( "Compiling arguments" );
            for ( int i = callExpression.Arguments.Count - 1; i >= 0; i-- )
            {
                if ( !TryCompileExpression( callExpression.Arguments[i] ) )
                {
                    LogError( callExpression.Arguments[i], "Failed to compile function call argument" );
                    return false;
                }
            }

            if ( mRootScope.TryGetFunction(callExpression.Identifier.Text, out var function))
            {
                // call function
                Emit( FlowScriptInstruction.COMM( function.Index ) );

                // push return value of fucntion
                // TODO: check if return value is used?
                if ( function.Declaration.ReturnType.ValueType != FlowScriptValueType.Void )
                    Emit( FlowScriptInstruction.PUSHREG() );
            }
            else if ( mRootScope.TryGetProcedure( callExpression.Identifier.Text, out var procedure ) )
            {
                // call procedure
                Emit( FlowScriptInstruction.CALL( procedure.Index ) );

                // value will already be on the script stack so no need to push anything
            }
            else
            {
                LogError( callExpression, "Invalid call expression. Expected function or procedure identifier" );
                return false;
            }

            return true;
        }

        private bool TryCompilePushVariableValue( FlowScriptIdentifier identifier )
        {
            LogInfo( identifier, $"Compiling variable reference: {identifier}" );

            if ( !CurrentScope.TryGetVariable( identifier.Text, out var variable ) )
            {
                LogError( identifier, $"Referenced undeclared variable '{identifier}'" );
                return false;
            }

            if ( variable.Declaration.Type.ValueType != FlowScriptValueType.Float )
            {
                Emit( FlowScriptInstruction.PUSHLIX( variable.Index ) );
            }
            else
            {
                Emit( FlowScriptInstruction.PUSHLFX( variable.Index ) );
            }

            return true;
        }

        private bool TryCompileUnaryExpression( FlowScriptUnaryExpression unaryExpression )
        {
            LogInfo( unaryExpression, $"Compiling unary expression: {unaryExpression}" );

            if ( !TryCompileExpression( unaryExpression.Operand ) )
            {
                LogError( unaryExpression.Operand, $"Unary expression operand failed to compile: {unaryExpression.Operand}" );
                return false;
            }

            if ( unaryExpression is FlowScriptLogicalNotOperator )
            {
                Emit( FlowScriptInstruction.NOT() );
            }
            else if ( unaryExpression is FlowScriptNegationOperator )
            {
                Emit( FlowScriptInstruction.MINUS() );
            }
            else
            {
                LogError( unaryExpression, $"Compiling unary expression '{unaryExpression}' not implemented" );
                return false;
            }

            return true;
        }

        private bool TryCompileBinaryExpression( FlowScriptBinaryExpression binaryExpression )
        {
            LogInfo( binaryExpression, $"Compiling binary expression: {binaryExpression}" );

            if ( binaryExpression is FlowScriptAssignmentOperator )
            {
                TryCompileVariableAssignment( ( ( FlowScriptIdentifier )binaryExpression.Left ), binaryExpression.Right );
            }
            else
            {
                if ( !TryCompileExpression( binaryExpression.Right ) )
                {
                    LogError( binaryExpression.Right, $"Right expression failed to compile: {binaryExpression.Left}" );
                    return false;
                }

                if ( !TryCompileExpression( binaryExpression.Left ) )
                {
                    LogError( binaryExpression.Right, $"Left expression failed to compile: {binaryExpression.Right}" );
                    return false;
                }

                if ( binaryExpression is FlowScriptAdditionOperator )
                {
                    Emit( FlowScriptInstruction.ADD() );
                }
                else if ( binaryExpression is FlowScriptSubtractionOperator )
                {
                    Emit( FlowScriptInstruction.SUB() );
                }
                else if ( binaryExpression is FlowScriptMultiplicationOperator )
                {
                    Emit( FlowScriptInstruction.MUL() );
                }
                else if ( binaryExpression is FlowScriptDivisionOperator )
                {
                    Emit( FlowScriptInstruction.DIV() );
                }
                else if ( binaryExpression is FlowScriptLogicalOrOperator )
                {
                    Emit( FlowScriptInstruction.OR() );
                }
                else if ( binaryExpression is FlowScriptLogicalAndOperator )
                {
                    Emit( FlowScriptInstruction.AND() );
                }
                else if ( binaryExpression is FlowScriptEqualityOperator )
                {
                    Emit( FlowScriptInstruction.EQ() );
                }
                else if ( binaryExpression is FlowScriptNonEqualityOperator )
                {
                    Emit( FlowScriptInstruction.NEQ() );
                }
                else if ( binaryExpression is FlowScriptLessThanOperator )
                {
                    Emit( FlowScriptInstruction.S() );
                }
                else if ( binaryExpression is FlowScriptGreaterThanOperator )
                {
                    Emit( FlowScriptInstruction.L() );
                }
                else if ( binaryExpression is FlowScriptLessThanOrEqualOperator )
                {
                    Emit( FlowScriptInstruction.SE() );
                }
                else if ( binaryExpression is FlowScriptGreaterThanOrEqualOperator )
                {
                    Emit( FlowScriptInstruction.LE() );
                }
                else
                {
                    LogError( binaryExpression, $"Compiling binary expression '{binaryExpression}' not implemented" );
                    return false;
                }
            }

            return true;
        }

        //
        // Literal values
        //
        private void EmitPushBoolLiteral( FlowScriptBoolLiteral boolLiteral)
        {
            if ( boolLiteral.Value )
                Emit( FlowScriptInstruction.PUSHIS( 1 ) );
            else
                Emit( FlowScriptInstruction.PUSHIS( 0 ) );
        }

        private void EmitPushIntLiteral( FlowScriptIntLiteral intLiteral )
        {
            if ( FitsInShort( intLiteral.Value ) )
                Emit( FlowScriptInstruction.PUSHIS( ( short )intLiteral.Value ) );
            else
                Emit( FlowScriptInstruction.PUSHI( intLiteral.Value ) );
        }

        private void EmitPushFloatLiteral( FlowScriptFloatLiteral floatLiteral)
        {
            Emit( FlowScriptInstruction.PUSHF( floatLiteral.Value ) );
        }

        private void EmitPushStringLiteral( FlowScriptStringLiteral stringLiteral )
        {
            Emit( FlowScriptInstruction.PUSHSTR( stringLiteral.Value ) );
        }

        private bool FitsInShort( int value )
        {
            return ( ( ( value & 0xffff8000 ) + 0x8000 ) & 0xffff7fff ) == 0;
        }

        // 
        // If statement
        //
        private bool TryCompileIfStatement( FlowScriptIfStatement ifStatement)
        {
            LogInfo( ifStatement, $"Compiling if statement: '{ifStatement}'" );

            // compile condition expression, which should push a boolean value to the stack
            if ( !TryCompileExpression( ifStatement.Condition ) )
            {
                LogError( ifStatement.Condition, "Failed to compile if statement condition" );
                return false;
            }

            // generate label for jump if condition is false
            var afterIfStatementLabel = DeclareLabel();
            Label elseLabel = null;

            if ( ifStatement.ElseBody == null )
            {
                // emit if instruction that jumps to the label if the condition is false
                Emit( FlowScriptInstruction.IF( afterIfStatementLabel.Index ) );
            }
            else
            {
                elseLabel = DeclareLabel();
                Emit( FlowScriptInstruction.IF( elseLabel.Index ) );
            }

            // compile body
            PushScope();

            CurrentScope.BreakLabel = afterIfStatementLabel;

            LogInfo( ifStatement.Body, "Compiling if statement body" );
            if ( !TryCompileCompoundStatement( ifStatement.Body ) )
            {
                LogError( ifStatement.Body, "Failed to compile if statement body" );
                return false;
            }

            // ensure that we end up at the right position after the body
            Emit( FlowScriptInstruction.GOTO( afterIfStatementLabel.Index ) );
            PopScope();

            if ( ifStatement.ElseBody != null )
            {
                ResolveLabel( elseLabel );

                // compile body
                PushScope();

                CurrentScope.BreakLabel = afterIfStatementLabel;

                LogInfo( ifStatement.Body, "Compiling else statement body" );
                if ( !TryCompileCompoundStatement( ifStatement.ElseBody ) )
                {
                    LogError( ifStatement.ElseBody, "Failed to compile else statement body" );
                    return false;
                }

                // ensure that we end up at the right position after the body
                Emit( FlowScriptInstruction.GOTO( afterIfStatementLabel.Index ) );
                PopScope();
            }

            ResolveLabel( afterIfStatementLabel );

            return true;
        }

        //
        // Return statement
        //
        private bool TryCompileReturnStatement( FlowScriptReturnStatement returnStatement )
        {
            LogInfo( returnStatement, $"Compiling return statement: '{returnStatement}'" );

            if ( returnStatement.Value != null )
            {
                // Save return address in a temporary variable
                var returnAddressSave = CurrentScope.GenerateVariable( FlowScriptValueType.Int, mNextIntVariableIndex++ );
                Emit( FlowScriptInstruction.POPLIX( returnAddressSave.Index ) );

                // Emit return value
                if ( !TryCompileExpression( returnStatement.Value ) )
                {
                    LogError( returnStatement.Value, $"Failed to compile return value: {returnStatement.Value}" );
                    return false;
                }

                // Pop saved return address back on the stack
                Emit( FlowScriptInstruction.PUSHLIX( returnAddressSave.Index ) );
            }

            // emit end
            Emit( FlowScriptInstruction.END() );
            return true;
        }

        //
        // Goto statement
        //
        private bool TryCompileGotoStatement( FlowScriptGotoStatement gotoStatement )
        {
            LogInfo( gotoStatement, $"Compiling goto statement: '{gotoStatement}'" );

            if ( !mLabels.TryGetValue(gotoStatement.LabelIdentifier.Text, out var label))
            {
                LogError( gotoStatement.LabelIdentifier, $"Goto statement referenced undeclared label: {gotoStatement.LabelIdentifier}" );
                return false;
            }

            // emit goto
            Emit( FlowScriptInstruction.GOTO( label.Index ) );
            return true;
        }

        private void Emit( FlowScriptInstruction instruction )
        {
            mInstructions.Add( instruction );
        }

        private Label DeclareLabel( string name = null )
        {
            var label = new Label();
            label.Index = (short)mLabels.Count;
            label.Name = name == null ? $"_{mNextLabelIndex++}" : name;

            mLabels.Add( label.Name, label );

            mLogger.Info( $"Generated label: {label.Name} index: {label.Index}" );

            return label;
        }

        private void ResolveLabel( Label label )
        {
            label.InstructionIndex = ( short )( mInstructions.Count );
            label.IsResolved = true;

            mLogger.Info( $"Resolved label {label.Name} to instruction index {label.InstructionIndex}" );
        }

        private void LogInfo( FlowScriptSyntaxNode node, string message )
        {
            if ( node.SourceInfo != null )
                mLogger.Info( $"{message} ({node.SourceInfo.Line}:{node.SourceInfo.Column})" );
            else
                mLogger.Info( message );
        }

        private void LogError( FlowScriptSyntaxNode node, string message )
        {
            if ( node.SourceInfo != null )
                mLogger.Error( $"{message} ({node.SourceInfo.Line}:{node.SourceInfo.Column})" );
            else
                mLogger.Error( message );

            if ( Debugger.IsAttached )
                Debugger.Break();
        }

        class Function
        {
            public FlowScriptFunctionDeclaration Declaration { get; set; }

            public short Index { get; set; }
        }

        class Procedure
        {
            public FlowScriptProcedureDeclaration Declaration { get; set; }

            public short Index { get; set; }
        }

        class Variable
        {
            public FlowScriptVariableDeclaration Declaration { get; set; }

            public short Index { get; set; }
        }

        class Label
        {
            public string Name { get; set; }

            public short Index { get; set; }

            public short InstructionIndex { get; set; }

            public bool IsResolved { get; set; }
        }

        class Scope
        {
            public Scope Parent { get; set; }

            public Dictionary<string, Function> Functions { get; set; }

            public Dictionary<string, Procedure> Procedures { get; set; }

            public Dictionary<string, Variable> Variables { get; set; }

            public Label BreakLabel { get; set; }

            public Label ContinueLabel { get; set; }

            public Scope( Scope parent )
            {
                Parent = parent;
                Functions = new Dictionary<string, Function>();
                Procedures = new Dictionary<string, Procedure>();
                Variables = new Dictionary<string, Variable>();
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

            public bool TryDeclareFunction( FlowScriptFunctionDeclaration declaration )
            {
                if ( TryGetFunction(declaration.Identifier.Text, out _))
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

            public Variable GenerateVariable( FlowScriptValueType type, short index )
            {
                var declaration = new FlowScriptVariableDeclaration(
                    new List<FlowScriptVariableModifier>() { new FlowScriptVariableModifier() },
                    new FlowScriptTypeIdentifier( type ),
                    new FlowScriptIdentifier(type, $"<>__generatedVariable{index}"),
                    null );

                Debug.Assert( TryDeclareVariable( declaration, index ) );
                Debug.Assert( TryGetVariable( declaration.Identifier.Text, out var variable ) );

                return variable;
            }
        }
    }
}