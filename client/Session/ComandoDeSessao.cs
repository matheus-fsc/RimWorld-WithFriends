using System.Collections.Generic;
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using WithFriends.Protocol.Messages;

namespace WithFriends.Client.Session;


/// <summary>
/// O que trafega dentro de <c>sessao.comando</c>.
///
/// Dentro da sessão **só comandos trafegam**, ambos simulam (§2.3). E a regra
/// que decide o que vira comando é a do lockstep:
///
/// > o que não é determinístico a partir do estado compartilhado vira comando.
///
/// Velocidade do tempo é o primeiro caso real. Ela não vive no estado
/// compartilhado — cada jogador clica no próprio botão — mas influencia
/// **quando** cada lado chega a cada tick. Com velocidades diferentes, os dois
/// lados atravessam os mesmos ticks em momentos distintos, e qualquer coisa
/// sensível a isso acontece em ticks diferentes.
/// </summary>
public static class ComandoDeSessao
{
    /// <summary>
    /// Enquanto <c>true</c>, estamos **aplicando** um comando: as intercepções
    /// deixam a ação passar em vez de transformá-la em comando de novo.
    /// </summary>
    public static bool Aplicando { get; private set; }

    public static byte[] Velocidade(TimeSpeed velocidade) =>
        new[] { (byte)TipoDeComando.Velocidade, (byte)velocidade };

    /// <summary>
    /// Ordem direta a um pawn. O <c>Job</c> é montado pela UI e não existe do
    /// outro lado, então viaja por valor — campos explícitos, não serialização
    /// profunda.
    ///
    /// Carregamos o que uma ordem manual usa. O que não está aqui se perde, e
    /// isso é escolha: formato explícito falha de forma visível, reflexão
    /// falha em silêncio.
    /// </summary>
    public static byte[] Ordem(int pawnId, Job job, JobTag? tag, bool enfileirar)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)TipoDeComando.OrdemDeTrabalho);
        w.Write(pawnId);
        w.Write(job.def?.defName ?? "");
        EscreverAlvo(w, job.targetA);
        EscreverAlvo(w, job.targetB);
        EscreverAlvo(w, job.targetC);
        w.Write(job.count);
        w.Write(job.playerForced);
        w.Write((int)job.haulMode);
        w.Write(job.expiryInterval);
        w.Write(job.checkOverrideOnExpire);
        w.Write(tag.HasValue);
        w.Write(tag.HasValue ? (int)tag.Value : 0);
        w.Write(enfileirar);
        return ms.ToArray();
    }

    /// <summary>
    /// Um alvo é uma coisa (id) **ou** uma célula. Ids de coisa valem dos dois
    /// lados porque a partida é a mesma (ADR 0010).
    /// </summary>
    static void EscreverAlvo(BinaryWriter w, LocalTargetInfo alvo)
    {
        if (alvo.HasThing)
        {
            w.Write((byte)1);
            w.Write(alvo.Thing.thingIDNumber);
        }
        else if (alvo.IsValid)
        {
            w.Write((byte)2);
            w.Write(alvo.Cell.x);
            w.Write(alvo.Cell.y);
            w.Write(alvo.Cell.z);
        }
        else
        {
            w.Write((byte)0);
        }
    }

    /// <summary>Remonta o designador do outro lado. Espelha <see cref="EscreverDesignador"/>.</summary>
    static (Designator? designador, bool godMode, string erro) LerDesignador(BinaryReader r)
    {
        string tipoNome = r.ReadString();
        var tipo = AccessTools.TypeByName(tipoNome);
        if (tipo == null) return (null, false, $"designador desconhecido ({tipoNome})");

        Designator? designador;
        if (r.ReadBoolean())
        {
            byte banco = r.ReadByte();
            string defName = r.ReadString();
            BuildableDef? def = banco == 2
                ? DefDatabase<TerrainDef>.GetNamedSilentFail(defName)
                : DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            string material = r.ReadString();
            int rot = r.ReadInt32();
            if (def == null) return (null, false, $"designador {tipoNome} sem def ({defName})");

            var construir = new Designator_Build(def);
            if (!string.IsNullOrEmpty(material))
                construir.SetStuffDef(DefDatabase<ThingDef>.GetNamedSilentFail(material));
            RotacaoDeColocacao.SetValue(construir, new Rot4(rot));
            designador = construir;
        }
        else
        {
            designador = Activator.CreateInstance(tipo) as Designator;
        }

        if (designador == null) return (null, false, $"não consegui remontar {tipoNome}");

        bool godMode = r.ReadBoolean();

        string corDefName = r.ReadString();
        if (corDefName.Length > 0)
        {
            var campoCor = AccessTools.Field(designador.GetType(), "colorDef");
            var cor = DefDatabase<ColorDef>.GetNamedSilentFail(corDefName);
            if (campoCor != null && cor != null) campoCor.SetValue(designador, cor);
        }

        return (designador, godMode, "");
    }

    static LocalTargetInfo LerAlvo(BinaryReader r)
    {
        return r.ReadByte() switch
        {
            1 => new LocalTargetInfo(EncontrarCoisa(r.ReadInt32())),
            2 => new LocalTargetInfo(new IntVec3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32())),
            _ => LocalTargetInfo.Invalid,
        };
    }

    static Thing? EncontrarCoisa(int thingIDNumber)
    {
        foreach (var mapa in Find.Maps)
        foreach (var coisa in mapa.listerThings.AllThings)
            if (coisa.thingIDNumber == thingIDNumber) return coisa;

        return null;
    }

    /// <summary>
    /// Um designador aplicado pelo jogador.
    ///
    /// O designador em si não viaja: ele é um objeto de UI, com estado de
    /// arrasto, ícone e modo conta-gotas. O que viaja é o suficiente para
    /// **remontar um igual** do outro lado — o tipo, e para construção o que
    /// se está construindo, de que material e virado para onde.
    /// </summary>
    /// <summary><c>Designator_Place.placingRot</c> é protegido; a rotação é
    /// parte do comando, então vai por reflexão.</summary>
    static readonly FieldInfo RotacaoDeColocacao =
        AccessTools.Field(typeof(Designator_Place), "placingRot");

    public static byte[] Designar(Designator designador, LocalTargetInfo alvo)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)TipoDeComando.Designar);
        EscreverDesignador(w, designador);
        EscreverAlvo(w, alvo);
        return ms.ToArray();
    }

    /// <summary>
    /// O arrasto inteiro num comando só.
    ///
    /// Uma parede de 40 células virava 40 comandos, 40 carimbos, e — com o jogo
    /// pausado — 40 passos de um tick. Agora é um comando, um tick, a parede
    /// inteira.
    /// </summary>
    public static byte[] DesignarVarias(Designator designador, IReadOnlyList<IntVec3> celulas)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)TipoDeComando.DesignarVarias);
        EscreverDesignador(w, designador);

        w.Write(celulas.Count);
        foreach (var celula in celulas)
        {
            w.Write(celula.x);
            w.Write(celula.y);
            w.Write(celula.z);
        }

        return ms.ToArray();
    }

    static void EscreverDesignador(BinaryWriter w, Designator designador)
    {
        w.Write(designador.GetType().FullName ?? "");

        var construir = designador as Designator_Build;
        w.Write(construir != null);
        if (construir != null)
        {
            // ThingDef e TerrainDef são bancos separados: sem dizer qual é,
            // "Wall" e "Bridge" iriam procurar no lugar errado.
            w.Write((byte)(construir.PlacingDef is TerrainDef ? 2 : 1));
            w.Write(construir.PlacingDef?.defName ?? "");
            w.Write(construir.StuffDef?.defName ?? "");
            w.Write(((Rot4)RotacaoDeColocacao.GetValue(construir)!).AsInt);
        }

        // god mode muda o que o designador faz: com ele a construção nasce
        // pronta, sem ele vira blueprint. Concordar sobre o comando não adianta
        // se os dois lados discordam sobre isso — então quem clicou manda.
        w.Write(DebugSettings.godMode);

        // A cor do planejador é escolha do jogador e mora no designador.
        // Reconstruindo do outro lado sem ela, o plano nasce com a cor padrão —
        // e, pior, `PlanAt(c).Color == colorDef` passa a dar outro resultado,
        // então o comando toma um caminho diferente em cada máquina.
        var campoCor = AccessTools.Field(designador.GetType(), "colorDef");
        w.Write(campoCor?.GetValue(designador) is Def cor ? cor.defName : "");
    }

    /// <summary>
    /// Provocar um incidente no mapa da visita.
    ///
    /// Viaja o def e os pontos; o resto dos parâmetros é derivado do estado
    /// compartilhado dos dois lados, então não precisa trafegar.
    /// </summary>
    public static byte[] Incidente(string defName, float pontos)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)TipoDeComando.Incidente);
        w.Write(defName);
        w.Write(pontos);
        return ms.ToArray();
    }

    public static byte[] Alistar(int pawnId, bool alistado)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)TipoDeComando.Alistar);
        w.Write(pawnId);
        w.Write(alistado);
        return ms.ToArray();
    }

    /// <summary>
    /// Aplica um comando já agendado pelo coordenador. Roda no mesmo tick nos
    /// dois lados, na ordem que o servidor carimbou.
    /// </summary>
    public static string Aplicar(byte[] payload)
    {
        if (payload.Length == 0) return "vazio";

        Aplicando = true;
        try
        {
            // Seleção é interface. Comando não pode depender do que este
            // jogador está olhando — ver SelecaoForaDoComando.
            return SelecaoForaDoComando.SemSelecao(() => Executar(payload));
        }
        finally
        {
            Aplicando = false;
        }
    }

    static string Executar(byte[] payload)
    {
        switch ((TipoDeComando)payload[0])
        {
            case TipoDeComando.Velocidade when payload.Length >= 2:
            {
                var velocidade = (TimeSpeed)payload[1];
                if (Find.TickManager != null)
                    ControleDeVelocidade.ComoSistema(
                        () => Find.TickManager.CurTimeSpeed = velocidade);
                return $"velocidade → {velocidade}";
            }

            case TipoDeComando.Alistar when payload.Length >= 6:
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var r = new BinaryReader(ms);
                r.ReadByte();
                int pawnId = r.ReadInt32();
                bool alistado = r.ReadBoolean();

                var pawn = Encontrar(pawnId);
                if (pawn?.drafter == null) return $"pawn {pawnId} não encontrado";

                pawn.drafter.Drafted = alistado;
                AlistamentoOtimista.Confirmar(pawn.thingIDNumber, alistado);
                return $"{pawn.LabelShort} {(alistado ? "alistado" : "desalistado")}";
            }

            case TipoDeComando.OrdemDeTrabalho:
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var r = new BinaryReader(ms);
                r.ReadByte();

                int pawnId = r.ReadInt32();
                string defName = r.ReadString();
                var pawn = Encontrar(pawnId);
                var def = DefDatabase<JobDef>.GetNamedSilentFail(defName);
                if (pawn?.jobs == null || def == null) return $"ordem inválida ({defName} para {pawnId})";

                var job = JobMaker.MakeJob(def);
                job.targetA = LerAlvo(r);
                job.targetB = LerAlvo(r);
                job.targetC = LerAlvo(r);
                job.count = r.ReadInt32();
                job.playerForced = r.ReadBoolean();
                job.haulMode = (HaulMode)r.ReadInt32();
                job.expiryInterval = r.ReadInt32();
                job.checkOverrideOnExpire = r.ReadBoolean();
                JobTag? tag = r.ReadBoolean() ? (JobTag)r.ReadInt32() : null;
                if (tag == null) r.ReadInt32();
                bool enfileirar = r.ReadBoolean();

                pawn.jobs.TryTakeOrderedJob(job, tag, enfileirar);
                return $"{pawn.LabelShort} → {def.defName}";
            }

            case TipoDeComando.DesignarVarias:
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var r = new BinaryReader(ms);
                r.ReadByte();

                var (designador, godModeDoAutor, erro) = LerDesignador(r);
                if (designador == null) return erro;

                int quantas = r.ReadInt32();
                var celulas = new List<IntVec3>(quantas);
                for (var i = 0; i < quantas; i++)
                    celulas.Add(new IntVec3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));

                bool antes = DebugSettings.godMode;
                DebugSettings.godMode = godModeDoAutor;

                // Quantas células o jogo aceita ANTES de aplicar.
                //
                // "Sete paredes, cinco construídas, buraco no meio" precisa de
                // um número para virar diagnóstico: se o jogo recusa células
                // aqui, o problema é o estado em que o comando chega; se aceita
                // todas e mesmo assim faltam, o problema é o que acontece
                // durante o laço.
                int aceitas = 0;
                string primeiraRecusa = "";
                foreach (var celula in celulas)
                {
                    var parecer = designador.CanDesignateCell(celula);
                    if (parecer.Accepted) aceitas++;
                    else if (primeiraRecusa.Length == 0)
                        primeiraRecusa = $"{celula}: {parecer.Reason ?? "(sem motivo)"}";
                }

                try
                {
                    // O próprio jogo faz o laço, com o CanDesignateCell e o
                    // Finalize de sempre — é o caminho dele, não uma imitação.
                    designador.DesignateMultiCell(celulas);
                }
                finally
                {
                    DebugSettings.godMode = antes;
                }

                string recusadas = aceitas == celulas.Count
                    ? ""
                    : $" — {celulas.Count - aceitas} recusada(s) pelo jogo, 1ª: {primeiraRecusa}";

                return $"{designador.GetType().Name} em {aceitas}/{celulas.Count} célula(s){recusadas}";
            }

            case TipoDeComando.Designar:
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var r = new BinaryReader(ms);
                r.ReadByte();

                var (designador, godModeDoAutor, erroDeLeitura) = LerDesignador(r);
                if (designador == null) return erroDeLeitura;

                var alvo = LerAlvo(r);
                if (!alvo.IsValid) return $"{designador.GetType().Name} sem alvo";

                bool godModeAntes = DebugSettings.godMode;
                DebugSettings.godMode = godModeDoAutor;
                try
                {
                    if (alvo.HasThing) designador.DesignateThing(alvo.Thing);
                    else designador.DesignateSingleCell(alvo.Cell);
                }
                finally
                {
                    DebugSettings.godMode = godModeAntes;
                }

                return $"{designador.GetType().Name} em " +
                       $"{(alvo.HasThing ? alvo.Thing.LabelShort : alvo.Cell.ToString())}";
            }

            case TipoDeComando.Incidente:
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var r = new BinaryReader(ms);
                r.ReadByte();

                string defName = r.ReadString();
                float pontos = r.ReadSingle();

                var def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
                var mapa = Find.CurrentMap;
                if (def == null || mapa == null) return $"incidente inválido ({defName})";

                var parametros = StorytellerUtility.DefaultParmsNow(def.category, mapa);
                parametros.points = pontos;

                bool aconteceu = def.Worker.TryExecute(parametros);
                return $"incidente {def.defName} ({pontos:F0} pts) → {(aconteceu ? "ok" : "não coube")}";
            }

            default:
                // §9.1 aplicado a comando: desconhecido é ignorado com log.
                return $"tipo desconhecido ({payload[0]})";
        }
    }

    /// <summary>
    /// Acha o pawn pelo id de coisa. Procura em todos os mapas porque a sessão
    /// pode envolver mais de um.
    /// </summary>
    static Pawn? Encontrar(int thingIDNumber)
    {
        foreach (var mapa in Find.Maps)
        foreach (var pawn in mapa.mapPawns.AllPawns)
            if (pawn.thingIDNumber == thingIDNumber) return pawn;

        return null;
    }
}

/// <summary>
/// Alistar é ordem do jogador: numa sessão ela **não acontece na hora**, vira
/// comando e acontece no tick que o coordenador carimbar — igual nos dois
/// lados (§2.3).
///
/// Medido: alistar sem sincronizar divergiu na hora. O tick do clique custou
/// 4 sorteios de diferença; dois ticks depois, quando o pawn largou o trabalho
/// e recalculou rota, a diferença virou 111.
///
/// É o primeiro membro do registro de comandos, e cada novo membro é dívida de
/// manutenção assumida de propósito (docs/SESSAO-COMPONENTES.md §3) — não um
/// custo herdado.
/// </summary>
[HarmonyPatch(typeof(RimWorld.Pawn_DraftController), nameof(RimWorld.Pawn_DraftController.Drafted), MethodType.Setter)]
public static class AlistarViraComando
{
    /// <summary>
    /// O campo por baixo do <c>Drafted</c>, para ler o valor real sem passar
    /// pelo palpite da interface.
    /// </summary>
    static readonly System.Reflection.FieldInfo? CampoAlistado =
        AccessTools.Field(typeof(RimWorld.Pawn_DraftController), "draftedInt");

    [HarmonyPrefix]
    public static bool Antes(RimWorld.Pawn_DraftController __instance, bool value)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando) return true;          // é o comando chegando

        // **Só o que vem da interface é ordem do jogador.**
        //
        // O jogo escreve em `Drafted` por conta própria dentro do tick — pawn
        // derrubado, job encerrado, mecânico desativado. Tratar isso como
        // clique bloqueava a lógica do jogo e enchia a tela de
        // "Jonas não é seu", sem ninguém ter clicado em nada.
        if (!NaInterface.Agora) return true;

        if (value == __instance.Drafted) return true;        // nada a fazer

        // Cada um comanda os próprios pawns (§4/§5).
        if (!PosseDePawns.EhMeu(__instance.pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(__instance.pawn);
            return false;
        }

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            Payload = ComandoDeSessao.Alistar(__instance.pawn.thingIDNumber, value),
        });

        // Valor REAL, sem passar pelo palpite.
        //
        // `pawn.Drafted` não serve para isto: ele passa pelo getter remendado,
        // que dentro da interface responde o palpite. O log dizia
        // "real agora: alistado" antes de qualquer comando ter chegado, o que
        // mandou a investigação para o lado errado por uma rodada.
        bool realAgora = CampoAlistado?.GetValue(__instance) is true;

        // O botão passa a mostrar o pedido na hora; a simulação segue esperando
        // o tick. Sem isto o gizmo continuava no estado antigo e o jogador
        // clicava de novo achando que tinha falhado.
        AlistamentoOtimista.Pedir(__instance.pawn.thingIDNumber, value);

        Log.Message(
            $"[WithFriends] {__instance.pawn.LabelShort}: " +
            $"{(value ? "alistar" : "desalistar")} proposto como comando " +
            $"(real agora: {(realAgora ? "alistado" : "livre")})");

        return false;   // a ordem só vale quando voltar carimbada
    }
}

/// <summary>
/// Toda ordem manual do jogador passa por aqui: clique com o botão direito,
/// "priorizar trabalho", mandar atacar. Numa sessão ela vira comando.
///
/// É o registro de maior alcance — uma intercepção cobre quase tudo o que um
/// jogador faz numa visita. O Multiplayer chega à mesma conclusão e sincroniza
/// `TryTakeOrderedJob` com `ExposeParameter(0)`, porque o `Job` é montado pela
/// UI e não existe do outro lado.
/// </summary>
[HarmonyPatch(typeof(Verse.AI.Pawn_JobTracker), nameof(Verse.AI.Pawn_JobTracker.TryTakeOrderedJob))]
public static class OrdemViraComando
{
    static readonly System.Reflection.FieldInfo? CampoDoPawn =
        AccessTools.Field(typeof(Verse.AI.Pawn_JobTracker), "pawn");

    [HarmonyPrefix]
    public static bool Antes(
        Verse.AI.Pawn_JobTracker __instance,
        Verse.AI.Job job,
        Verse.AI.JobTag? tag,
        bool requestQueueing,
        ref bool __result)
    {
        var sessao = Colony.SincronizacaoComponent.Atual?.Sessao;
        if (sessao is not { Estado: EstadoSessaoLocal.Simulando } || sessao.Atual == null) return true;
        if (ComandoDeSessao.Aplicando) return true;   // é o comando chegando

        // Idem: `TryTakeOrderedJob` também é chamado de dentro do jogo, ao
        // puxar o próximo da fila —
        //
        //     pawn.jobs.TryTakeOrderedJob(queuedJob.job, queuedJob.tag, requestQueueing: true)
        //
        // Aquilo é a simulação andando, não o jogador mandando.
        if (!NaInterface.Agora) return true;

        if (job?.def == null) return true;

        // `pawn` é campo protegido do tracker: alcançado por reflexão e
        // catalogado, como todo acoplamento interno.
        var pawn = CampoDoPawn?.GetValue(__instance) as Pawn;
        if (pawn == null) return true;

        if (!PosseDePawns.EhMeu(pawn))
        {
            PosseDePawns.AvisarQueNaoEhSeu(pawn);
            __result = false;
            return false;
        }

        WithFriendsMod.Cliente.Enviar(new Protocol.Messages.SessaoComando
        {
            SessaoId = sessao.Atual.SessaoId,
            // O `requestQueueing` do chamador quase sempre é false: quem decide
            // empilhar, no vanilla, é o teclado lido lá dentro. Aqui essa
            // leitura acontece UMA vez, na máquina de quem clicou, e viaja no
            // comando — ver TecladoForaDaSimulacao.
            Payload = ComandoDeSessao.Ordem(
                pawn.thingIDNumber, job, tag,
                requestQueueing || RimWorld.KeyBindingDefOf.QueueOrder.IsDownEvent),
        });

        Log.Message($"[WithFriends] ordem {job.def.defName} para {pawn.LabelShort} proposta como comando");

        // A UI recebe "aceito" para não mostrar recusa; a ordem de verdade
        // acontece quando voltar carimbada, no mesmo tick dos dois lados.
        __result = true;
        return false;
    }
}
