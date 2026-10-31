using FluentAssertions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;
using Xunit;

namespace OmegaConf.NET.Tests;

public class OperatorTests
{
    [Fact]
    public void OperatorParser_ShouldExtractSuffixOperators()
    {
        OperatorParser.ParseKey("timeout+=").Should().Be(("timeout", ConfigOperator.Add));
        OperatorParser.ParseKey("items-=").Should().Be(("items", ConfigOperator.Subtract));
        OperatorParser.ParseKey("scale*=").Should().Be(("scale", ConfigOperator.Multiply));
        OperatorParser.ParseKey("ratio/=").Should().Be(("ratio", ConfigOperator.Divide));
        OperatorParser.ParseKey("flag&=").Should().Be(("flag", ConfigOperator.BitwiseAnd));
        OperatorParser.ParseKey("flag|=").Should().Be(("flag", ConfigOperator.BitwiseOr));
        OperatorParser.ParseKey("debug!=").Should().Be(("debug", ConfigOperator.Toggle));
        OperatorParser.ParseKey("normal=").Should().Be(("normal", ConfigOperator.Assign));
        OperatorParser.ParseKey("plain_key").Should().Be(("plain_key", ConfigOperator.Assign));
    }

    [Fact]
    public void AddOperator_ShouldAddNumbers_AndAppendToLists_AndConcatStrings()
    {
        // Numeric Add
        var baseNum = new ConfigValueNode(10L);
        var overrideNum = new ConfigValueNode(5L, ConfigOperator.Add);
        var resNum = (ConfigValueNode)OperatorEngine.Apply(baseNum, overrideNum);
        resNum.Value.Should().Be(15L);

        // String Concat
        var baseStr = new ConfigValueNode("Hello, ");
        var overrideStr = new ConfigValueNode("World!", ConfigOperator.Add);
        var resStr = (ConfigValueNode)OperatorEngine.Apply(baseStr, overrideStr);
        resStr.Value.Should().Be("Hello, World!");

        // List Append
        var baseList = new ConfigListNode([new ConfigValueNode("item1")]);
        var overrideList = new ConfigListNode([new ConfigValueNode("item2")], ConfigOperator.Add);
        var resList = (ConfigListNode)OperatorEngine.Apply(baseList, overrideList);
        resList.Items.Should().HaveCount(2);
        resList.Items[0].ToString().Should().Be("item1");
        resList.Items[1].ToString().Should().Be("item2");
    }

    [Fact]
    public void SubtractOperator_ShouldSubtractNumbers_AndRemoveListItems()
    {
        // Numeric Subtract
        var baseNum = new ConfigValueNode(20L);
        var overrideNum = new ConfigValueNode(7L, ConfigOperator.Subtract);
        var resNum = (ConfigValueNode)OperatorEngine.Apply(baseNum, overrideNum);
        resNum.Value.Should().Be(13L);

        // List item removal
        var baseList = new ConfigListNode([new ConfigValueNode("a"), new ConfigValueNode("b"), new ConfigValueNode("c")]);
        var overrideList = new ConfigListNode([new ConfigValueNode("b")], ConfigOperator.Subtract);
        var resList = (ConfigListNode)OperatorEngine.Apply(baseList, overrideList);
        resList.Items.Should().HaveCount(2);
        resList.Items.Select(x => x.ToString()).Should().ContainInOrder("a", "c");
    }

    [Fact]
    public void MultiplyAndDivideOperators_ShouldWorkOnNumbers()
    {
        // Multiply
        var baseNum = new ConfigValueNode(25L);
        var multNum = new ConfigValueNode(4L, ConfigOperator.Multiply);
        var resMult = (ConfigValueNode)OperatorEngine.Apply(baseNum, multNum);
        resMult.Value.Should().Be(100L);

        // Divide
        var divNum = new ConfigValueNode(5L, ConfigOperator.Divide);
        var resDiv = (ConfigValueNode)OperatorEngine.Apply(resMult, divNum);
        resDiv.Value.Should().Be(20L);

        // String repetition
        var baseStr = new ConfigValueNode("ha");
        var repNum = new ConfigValueNode(3, ConfigOperator.Multiply);
        var resRep = (ConfigValueNode)OperatorEngine.Apply(baseStr, repNum);
        resRep.Value.Should().Be("hahaha");
    }

    [Fact]
    public void BooleanOperators_ShouldApplyLogicCorrectly()
    {
        // AND
        var boolTrue = new ConfigValueNode(true);
        var boolFalse = new ConfigValueNode(false, ConfigOperator.BitwiseAnd);
        var resAnd = (ConfigValueNode)OperatorEngine.Apply(boolTrue, boolFalse);
        resAnd.Value.Should().Be(false);

        // OR
        var boolFalseBase = new ConfigValueNode(false);
        var boolTrueOverride = new ConfigValueNode(true, ConfigOperator.BitwiseOr);
        var resOr = (ConfigValueNode)OperatorEngine.Apply(boolFalseBase, boolTrueOverride);
        resOr.Value.Should().Be(true);

        // Toggle (!=)
        var toggle = new ConfigValueNode(true, ConfigOperator.Toggle);
        var resToggle = (ConfigValueNode)OperatorEngine.Apply(boolTrue, toggle);
        resToggle.Value.Should().Be(false);
    }

    [Fact]
    public void IncompatibleOperator_ShouldThrowInvalidOperatorException()
    {
        var baseVal = new ConfigValueNode("not a boolean");
        var toggle = new ConfigValueNode(true, ConfigOperator.Toggle);

        var act = () => OperatorEngine.Apply(baseVal, toggle);
        act.Should().Throw<InvalidOperatorException>();
    }
}
