using WithFriends.Client.Auditoria;
using Xunit;

namespace WithFriends.Tests;

/// <summary>
/// O auditor varre o jogo, que os testes não têm. O que dá para travar aqui é a
/// **regra** — e é ela que precisa sobreviver a atualização do RimWorld.
/// </summary>
public class AuditoriaTests
{
    [Fact]
    public void Tipo_inteiro_vigiado_casa_qualquer_membro()
    {
        // UnityEngine.Input é local por inteiro: não há membro dele que dois
        // jogadores respondam igual.
        Assert.NotNull(FontesLocais.Casar("UnityEngine.Input", "get_mousePosition"));
        Assert.NotNull(FontesLocais.Casar("UnityEngine.Input", "GetKeyDown"));
    }

    [Fact]
    public void Tipo_com_lista_casa_so_os_membros_listados()
    {
        // DateTime tem muito membro inocente; só o relógio é fonte local.
        Assert.NotNull(FontesLocais.Casar("System.DateTime", "get_Now"));
        Assert.Null(FontesLocais.Casar("System.DateTime", "AddDays"));
        Assert.Null(FontesLocais.Casar("System.DateTime", "get_Year"));
    }

    [Fact]
    public void Find_vigia_o_foco_do_jogador_e_nao_o_resto()
    {
        // Find.CurrentMap é o mapa que ESTE jogador abriu; Find.TickManager é
        // estado compartilhado e não pode entrar na lista.
        Assert.NotNull(FontesLocais.Casar("Verse.Find", "get_CurrentMap"));
        Assert.NotNull(FontesLocais.Casar("Verse.Find", "get_Selector"));
        Assert.Null(FontesLocais.Casar("Verse.Find", "get_TickManager"));
        Assert.Null(FontesLocais.Casar("Verse.Find", "get_World"));
    }

    [Fact]
    public void Propriedade_casa_escrita_com_ou_sem_get()
    {
        // No IL, ler propriedade é chamar get_Nome. A regra aceita as duas
        // formas — escrever só uma fazia a fonte casar com NADA, e o relatório
        // dizia "zero chamadores" para algo com dezenas. Foi o que aconteceu
        // com KeyBindingDef na primeira execução do auditor.
        Assert.NotNull(FontesLocais.Casar("Verse.KeyBindingDef", "IsDownEvent"));
        Assert.NotNull(FontesLocais.Casar("Verse.KeyBindingDef", "get_IsDownEvent"));
        Assert.NotNull(FontesLocais.Casar("System.DateTime", "get_Now"));
        Assert.NotNull(FontesLocais.Casar("System.DateTime", "Now"));
    }

    [Fact]
    public void Tipo_desconhecido_nao_casa()
    {
        Assert.Null(FontesLocais.Casar("Verse.Thing", "Tick"));
        Assert.Null(FontesLocais.Casar(null, "Tick"));
    }

    [Fact]
    public void As_fontes_que_ja_neutralizamos_estao_marcadas()
    {
        // Se alguém remover o remendo do teclado, este teste não pega — mas o
        // relatório passaria a mentir dizendo "já neutralizada". A marcação é
        // afirmação de fato e merece ficar visível.
        // Fonte tratada na fonte cobre todo chamador, hoje e no futuro.
        var teclado = FontesLocais.Casar("Verse.KeyBindingDef", "IsDownEvent");
        Assert.Equal(Tratamento.Fonte, teclado!.Estado);

        // Time é nativa: só os chamadores conhecidos estão tratados, e a
        // distinção tem de sobreviver — senão um chamador novo numa versão
        // futura aparece no relatório com cara de resolvido.
        var tempo = FontesLocais.Casar("UnityEngine.Time", "get_deltaTime");
        Assert.Equal(Tratamento.Chamadores, tempo!.Estado);

        var entrada = FontesLocais.Casar("UnityEngine.Input", "GetKey");
        Assert.Equal(Tratamento.Aberta, entrada!.Estado);
    }

    [Theory]
    [InlineData("RimWorld.Dialog_FormCaravan", "DoWindowContents", true)]
    [InlineData("Verse.Pawn_PathFollower", "PatherTick", false)]
    [InlineData("RimWorld.Alert_Hypothermia", "GetReport", true)]
    [InlineData("RimWorld.JobGiver_Wander", "TryGiveJob", false)]
    public void Heuristica_de_interface_separa_a_leitura(string tipo, string metodo, bool esperado)
    {
        Assert.Equal(esperado, FontesLocais.CheiraAInterface(tipo, metodo));
    }
}
