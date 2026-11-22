// Official getParam/checkSubmit/encryptParam snapshot from 10.20.16.5/portal/portalUtil.js, fetched 2026-11-22. Tests stub AJAX and RSA primitives; no campus requests.
function getParam(paramName) {
    var url = window.location.search;
    // 正则筛选地址栏
    var reg = new RegExp("(^|&)" + paramName + "=([^&]*)(&|$)");
    // 匹配目标参数
    var result = url.substr(1).match(reg);
    //返回参数值
    try {
        return result ? decodeURIComponent(result[2]) : '';
    } catch (e) {
        return result[2];
    }
}

function checkSubmit() {
    // _alert("上线");
    showLoading("正在认证..");
    if ($.trim($(portalconfig.username).val()) == "") {
        _alert(local.username + local.notnull);
        $(portalconfig.username).focus();
        hideLoading();
        return;
    }
    if ($.trim($(portalconfig.password).val()) == "") {
        _alert(local.passwd + local.notnull);
        $(portalconfig.password).focus();
        hideLoading();
        return;
    }
    // if ($.trim($(portalconfig.password).val()).length > 15) {
    //     _alert(local.passwd + '不支持15位以上的密码');
    //     $(portalconfig.password).focus();
    //     hideLoading();
    //     return;
    // }
    if (portalconfig.portalconfig && portalconfig.portalconfig.listpasscode && portalconfig.portalconfig.listpasscode != 0) {
        if ($(portalconfig.validateCode).val() == "") {
            _alert(local.validateCode + local.notnull);
            $(portalconfig.validateCode).focus();
            hideLoading();
            return;
        }
    }

    if ($(portalconfig.read)) {
        debug("read:" + $(portalconfig.read).is(":checked"));
        if (!$(portalconfig.read).is(":checked")) {
            _alert(local.readProtocol);
            hideLoading();
            return;
        }
    }

    var postdata = {
        'userid': $(portalconfig.username).val().replace(/\s*/g, ""),
        'passwd': $(portalconfig.password).val().replace(/\s*/g, ""),
        'wlanuserip': portalconfig.portalForm.wlanuserip ? portalconfig.portalForm.wlanuserip : getParam('wlanuserip') || portalconfig.wlanuserip,
        'wlanuseripv6': getParam('wlanuseripv6') || portalconfig.wlanuseripv6,
        'wlanacname': portalconfig.portalForm.wlanacname ? portalconfig.portalForm.wlanacname : getParam('wlanacname'),
        'wlanacIp': getParam('wlanacIp') || portalconfig.serverForm ? portalconfig.serverForm.serverip : "",
        'ssid': portalconfig.portalForm.ssid ? portalconfig.portalForm.ssid : getParam('ssid') || '',
        'vlan': portalconfig.portalForm.vlan ? portalconfig.portalForm.vlan : getParam('vlan') || '',
        'mac': portalconfig.portalForm.mac ? portalconfig.portalForm.mac : getParam('wlanstamac') || getParam('usermac') || getParam('mac') || '',
        "version": portalconfig.serverForm ? portalconfig.serverForm.portalVer : "",
        'portalpageid': portalconfig.portalconfig ? portalconfig.portalconfig.id : '',
        "validateCode": $(portalconfig.validateCode).val(),
        "timestamp": (portalconfig.portalconfig && portalconfig.portalconfig.timestamp) ? portalconfig.portalconfig.timestamp : "",
        "uuid": (portalconfig.portalconfig && portalconfig.portalconfig.uuid) ? portalconfig.portalconfig.uuid : "",
        "portaltype": $(portalconfig.portaltype).val() || portalconfig.portaltypeDefaultVal || "",
        "hostname": getParam("hostname") || '',
        "bindCtrlId": $("#choose_ctrl_select input[name='bindCtrlId']:checked").val() || $("#bind_ctrl_select input[name='bindCtrlId']:checked").val() || $("#bind_ctrl_select select[name='bindCtrlId'] option:selected").val() || '',
        "validateType": $("#validateType").val() || portalconfig.validateType,
        "bindOperatorType": portalconfig.skipTestPPPoE ? '1' : '2'
    };

    encryptParam(postdata);
    portalconfig.skipTestPPPoE = false
    // console.log(JSON.stringify(postdata));
    $.ajax({
        url: '/quickauth.do',
        type: 'get',
        dataType: 'json',
        async: false,
        data: postdata,
        success: function (json) {
            hideLoading();
            debug("success:" + JSON.stringify(json));
            if (json.code == "0") {
                // _alert("认证成功");

                if (portalconfig.portalconfig && portalconfig.portalconfig.logoutgourl != "") {

                    if (portalconfig.portalconfig.logoutgourl.indexOf("logout") > -1) {
                        // _alert(portalconfig.portalParam.wlanacip);
                        // _alert(getParam(wlanacip));
                        // _alert(json.message);
                        if (window.localStorage) {
                            window.localStorage.setItem("userid", $(portalconfig.username).val());
                            window.localStorage.setItem("groupId", json.groupId);
                        }

                        var url = portalconfig.portalconfig.logoutgourl + "?wlanacip=" + postdata.wlanacIp + "&wlanuserip=" + postdata.wlanuserip + "&wlanacname=" + postdata.wlanacname + "&mac=" + postdata.mac + "&version=" + postdata.version + "&msg=" + (json.message ? encodeURIComponent(json.message) : "") + "&selfTicket=" + (json.selfTicket || '') + "&macChange=" + (json.macChange || "false") + "&dropLogCheck=" + json.dropLogCheck + "&vlan=" + postdata.vlan;
                        if (json.groupId && json.groupId !== '') {
                            url += "&groupId=" + json.groupId;
                        }
                        if (json.userId && json.userId != "") {
                            url += "&userId=" + json.userId;
                        } else {
                            url += "&userId=" + $(portalconfig.username).val();
                        }
                        // window.location.href=url;
                        checkPasswdPolicy(json, url);

                        // window.close();
                    } else {
                        // _alert(portalconfig.portalconfig.logoutgourl);

                        // window.location.href = portalconfig.portalconfig.logoutgourl;

                        checkPasswdPolicy(json, portalconfig.portalconfig.logoutgourl);
                        // window.close();

                    }

                } else {
                    if (json.logoutgourl) {

                        var url = json.logoutgourl + "?wlanacip=" + postdata.wlanacIp + "&wlanuserip=" + postdata.wlanuserip + "&wlanacname=" + postdata.wlanacname + "&mac=" + postdata.mac + "&version=" + postdata.version + "&msg=" + (json.message ? encodeURIComponent(json.message) : "") + "&selfTicket=" + (json.selfTicket || '') + "&macChange=" + (json.macChange || "false") + "&dropLogCheck=" + json.dropLogCheck;
                        if (json.groupId && json.groupId !== '') {
                            url += "&groupId=" + json.groupId;
                        }
                        if (json.userId && json.userId != "") {
                            url += "&userId=" + json.userId;
                        } else {
                            url += "&userId=" + $(portalconfig.username).val();
                        }
                        // window.location.href = url;
                        checkPasswdPolicy(json, url);

                    } else {

                        // window.location.href = getParam("url");
                        checkPasswdPolicy(json, getParam("url"));

                    }
                }
            } else if (json.code == "201") {
                window.location.href = json.logoutgourl;

            } else if (json.code === "235") {
                _confirm('温馨提示', json.message, '修改密码', '取消', function () {
                    $("._confirm_btn__btn_confirm").bind('click', function () {
                        window.location.href = "/";
                    })
                })
            } else if (json.code == "236") {
                hideLoading();
                _alert(json.message, 3000);
                setTimeout(function () {
                    $(".bind_only_modal").show();
                    $("#bindDxId").focus();
                }, 3000);
            } else if (json.code == "237") {
                hideLoading();
                _alert(json.message, 3000);
                if (portalconfig.initBindModal && typeof portalconfig.initBindModal == "function") {
                    console.log("init bind modal")
                    portalconfig.initBindModal(portalconfig.portalconfig.operatorBindingPolicy == '2' ? json.operatingBindCtrlList : []);

                } else {
                    initBindModal(portalconfig.portalconfig.operatorBindingPolicy == '2' ? json.operatingBindCtrlList : []);
                }
                setTimeout(function () {
                    bind_ctrl_modal_step_switch();
                    $("#bindCtrlDxId").val("")
                    $("#bindCtrlDxNo").val("")
                    $(".bind_mask,.bind_ctrl_modal").show();
                }, 3000);
            } else if (json.code == "238") {
                hideLoading();
                _alert(json.message, 3000);
                setTimeout(function () {
                    $(".bind_mask,.choose_ctrl_modal").show();
                }, 3000);
            } else if (json.code == '241') {
                hideLoading();
                portalconfig.skipTestPPPoE = true;
                _alert(json.message, 3000);
                if (portalconfig.portalconfig.operatorBindingPolicy == '2') {
                    if (portalconfig.initBindModal && typeof portalconfig.initBindModal == "function") {
                        console.log("init bind modal")
                        portalconfig.initBindModal(json.operatingBindCtrlList);

                    } else {
                        initBindModal(json.operatingBindCtrlList);
                    }
                }
                setTimeout(function () {
                    bind_ctrl_modal_step_switch();
                    $("#bindCtrlDxId").val("")
                    $("#bindCtrlDxNo").val("")
                    $(".bind_mask,.bind_ctrl_modal").show();
                }, 3000);
            } else if (json.code == '242') {
                if (portalconfig.huashangeShowModal && typeof portalconfig.huashangeShowModal == "function") {
                    portalconfig.huashangeShowModal(json.message);
                } else {
                    _alert(json.message, 5000);
                }

            } else if (json.code == "-1") {
                hideLoading();
                _alert(json.message, 5000);
                if (tryrelogintime < 2) {
                    setTimeout(function () {
                        tryrelogintime++;
                        checkSubmit();
                    }, 5000);
                } else {
                    _alert("认证失败,超过自动重试次数", 5000);
                }

            } else {
                $("#bind_ctrl_select input[name='bindCtrlId']").removeAttr('checked')
                _alert(json.message);
            }
        },
        error: function (json) {
            hideLoading();
            debug("error");
            _alert(portalconfig.useLocal && local && local.loginfail ? local.loginfail : "登陆失败，请稍后再试！");
        }

    });

}

function encryptParam(param) {
    if (portalconfig.authByRas && portalconfig.rsa && portalconfig.rsa.modulus && portalconfig.rsa.publicExponent) {
        try {
            param.modulus = portalconfig.rsa.modulus;
            setMaxDigits(130);
            var key = new RSAKeyPair(portalconfig.rsa.publicExponent, "", portalconfig.rsa.modulus);
            if (param.userid && param.userid != '') {
                param.userid = encryptedString(key, param.userid);
            }
            if (param.passwd && param.passwd != '') {
                param.passwd = encryptedString(key, param.passwd);
            }
            if (param.dxid && param.dxid != '') {
                param.dxid = encryptedString(key, param.dxid);
            }
            if (param.dxno && param.dxno != '') {
                param.dxno = encryptedString(key, param.dxno);
            }
        } catch (e) {
            _alert("加密失败,请刷新页面后重新认证")
        }
    }
}
